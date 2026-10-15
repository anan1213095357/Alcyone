using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Text;
using System.Text.Json;

namespace StateMachine.Scripting;

internal static class ScriptTraceInstrumentation
{
    private sealed record Function(SyntaxNode Node, BlockSyntax? Body, ArrowExpressionClauseSyntax? ExpressionBody, TypeSyntax ReturnType, ParameterListSyntax ParameterList, SyntaxTokenList Modifiers, SyntaxToken Identifier, SyntaxToken SemicolonToken)
    {
        public int SpanStart => Node.SpanStart;
        public Microsoft.CodeAnalysis.Text.TextSpan Span => Node.Span;
    }
    private static IEnumerable<Function> Functions(SyntaxNode root) => root.DescendantNodes().Select(n => n switch
    {
        MethodDeclarationSyntax m => new Function(m, m.Body, m.ExpressionBody, m.ReturnType, m.ParameterList, m.Modifiers, m.Identifier, m.SemicolonToken),
        LocalFunctionStatementSyntax m => new Function(m, m.Body, m.ExpressionBody, m.ReturnType, m.ParameterList, m.Modifiers, m.Identifier, m.SemicolonToken),
        _ => null
    }).OfType<Function>();
    internal static int[] BreakableLines(SyntaxTree tree, int requested)
    {
        var lines = new SortedSet<int>();
        foreach (var method in Functions(tree.GetRoot()))
        {
            if (method.ReturnType is RefTypeSyntax || method.ParameterList.Parameters.Any(p => p.Modifiers.Any(SyntaxKind.OutKeyword) || p.Modifiers.Any(SyntaxKind.RefKeyword)) || method.Body?.DescendantNodes().Any(n => n is YieldStatementSyntax or GotoStatementSyntax) == true) continue;
            var span = tree.GetLineSpan(method.Span);
            if (requested < span.StartLinePosition.Line + 1 || requested > span.EndLinePosition.Line + 1) continue;
            if (method.ExpressionBody is not null) lines.Add(tree.GetLineSpan(method.ExpressionBody.Expression.Span).StartLinePosition.Line + 1);
            if (method.Body is null) continue;
            lines.Add(tree.GetLineSpan(method.Body.OpenBraceToken.Span).StartLinePosition.Line + 1);
            foreach (var statement in method.Body.DescendantNodes().OfType<StatementSyntax>())
                if (IsSequencePoint(statement, method.Node)) lines.Add(tree.GetLineSpan(statement.Span).StartLinePosition.Line + 1);
        }
        return lines.ToArray();
    }
    private static bool IsSequencePoint(StatementSyntax statement, SyntaxNode function) =>
        statement is not (BlockSyntax or LocalFunctionStatementSyntax) &&
        (statement.Parent is BlockSyntax or SwitchSectionSyntax or IfStatementSyntax or ElseClauseSyntax or WhileStatementSyntax or DoStatementSyntax or ForStatementSyntax or ForEachStatementSyntax or UsingStatementSyntax or LockStatementSyntax) &&
        !statement.Ancestors().TakeWhile(n => n != function).Any(n => n is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax);
    public static CSharpCompilation Apply(CSharpCompilation compilation)
    {
        foreach (var tree in compilation.SyntaxTrees.ToArray())
        {
            if (Path.GetFileName(tree.FilePath).StartsWith("__HotScript")) continue;
            var semantic = compilation.GetSemanticModel(tree);
            var root = tree.GetRoot();
            var edits = new List<(int At, string Text)>();
            var replacements = new List<(int At, int Length, string Text)>();
            string Quote(string value) => JsonSerializer.Serialize(value);
            foreach (var method in Functions(root))
            {
                var methodSymbol = semantic.GetDeclaredSymbol(method.Node) as IMethodSymbol;
                var apiArgument = "null";
                if (methodSymbol?.IsStatic == false && !method.Node.Ancestors().Any(n => n is MethodDeclarationSyntax m && m.Modifiers.Any(SyntaxKind.StaticKeyword) || n is LocalFunctionStatementSyntax l && l.Modifiers.Any(SyntaxKind.StaticKeyword)))
                    for (var type = methodSymbol.ContainingType; type is not null; type = type.BaseType)
                        if (type.ToDisplayString() == "StateMachine.Scripting.StateScript") { apiArgument = "this.Api"; break; }
                if (method.ExpressionBody is not null && method.ReturnType is not RefTypeSyntax && !method.ParameterList.Parameters.Any(p => p.Modifiers.Any(SyntaxKind.OutKeyword) || p.Modifiers.Any(SyntaxKind.RefKeyword)))
                {
                    var expression = method.ExpressionBody.Expression;
                    var symbol = methodSymbol;
                    var isAsync = method.Modifiers.Any(SyntaxKind.AsyncKeyword);
                    var returnsVoid = symbol?.ReturnsVoid == true || isAsync && symbol?.ReturnType is INamedTypeSymbol named && named.TypeArguments.Length == 0 && named.Name is "Task" or "ValueTask";
                    var line = tree.GetLineSpan(expression.Span).StartLinePosition.Line + 1;
                    var scopeName = "__alcyoneExpression" + method.SpanStart;
                    var checkpointCall = isAsync ? "await global::StateMachine.Scripting.ScriptTrace.CheckpointAsync();" : "global::StateMachine.Scripting.ScriptTrace.Checkpoint();";
                    var failCall = isAsync ? "await global::StateMachine.Scripting.ScriptTrace.ExceptionAsync(__traceError);" : "global::StateMachine.Scripting.ScriptTrace.Exception(__traceError);";
                    var parameters = method.ParameterList.Parameters.Where(p => semantic.GetDeclaredSymbol(p)?.Type.IsRefLikeType == false).Select(p => $"({Quote(p.Identifier.ValueText)},(object?)@{p.Identifier.ValueText})");
                    var body = $"{{\n#line hidden\nusing var {scopeName} = global::StateMachine.Scripting.ScriptTrace.Enter({Quote(tree.FilePath)}, {Quote(method.Identifier.ValueText)}, {apiArgument});\ntry {{\nif(global::StateMachine.Scripting.ScriptTrace.Step({line},{Quote(expression.ToString())})) global::StateMachine.Scripting.ScriptTrace.Values({string.Join(",", parameters)});\n{checkpointCall}\n#line {line} {Quote(tree.FilePath)}\n{(returnsVoid || expression is ThrowExpressionSyntax ? "" : "return ")}{expression};\n#line hidden\n}} catch(global::System.Exception __traceError) {{ {scopeName}.Fail(__traceError); {failCall} throw; }}\n}}\n#line {tree.GetLineSpan(method.Span).EndLinePosition.Line + 1} {Quote(tree.FilePath)}\n";
                    replacements.Add((method.ExpressionBody.SpanStart, method.SemicolonToken.Span.End - method.ExpressionBody.SpanStart, body));
                    continue;
                }
                if (method.Body is null || method.ReturnType is RefTypeSyntax || method.ParameterList.Parameters.Any(p => p.Modifiers.Any(SyntaxKind.OutKeyword) || p.Modifiers.Any(SyntaxKind.RefKeyword)) || method.Body.DescendantNodes().Any(n => n is YieldStatementSyntax or GotoStatementSyntax)) continue;
                var scope = "__alcyoneTrace" + method.SpanStart;
                var error = scope + "Error";
                var methodName = method.Identifier.ValueText;
                var asyncMethod = method.Modifiers.Any(SyntaxKind.AsyncKeyword);
                var checkpoint = asyncMethod ? "await global::StateMachine.Scripting.ScriptTrace.CheckpointAsync();" : "global::StateMachine.Scripting.ScriptTrace.Checkpoint();";
                var exceptionCall = asyncMethod ? $"await global::StateMachine.Scripting.ScriptTrace.ExceptionAsync({error});" : $"global::StateMachine.Scripting.ScriptTrace.Exception({error});";
                var entryLine = tree.GetLineSpan(method.Body.OpenBraceToken.Span).StartLinePosition.Line + 1;
                var entryValues = method.ParameterList.Parameters.Where(p => semantic.GetDeclaredSymbol(p)?.Type.IsRefLikeType == false).Select(p => $"({Quote(p.Identifier.ValueText)},(object?)@{p.Identifier.ValueText})").ToList();
                if (apiArgument == "this.Api") entryValues.Add("(\"this\", (object?)this)");
                var entryCheckpoint = $"if(global::StateMachine.Scripting.ScriptTrace.Step({entryLine},{Quote(methodName + "()")})) global::StateMachine.Scripting.ScriptTrace.Values({string.Join(",", entryValues)});\n{checkpoint}\n";
                edits.Add((method.Body.OpenBraceToken.Span.End, $"\n#line hidden\nusing var {scope} = global::StateMachine.Scripting.ScriptTrace.Enter({Quote(tree.FilePath)}, {Quote(methodName)}, {apiArgument});\ntry {{\n{entryCheckpoint}"));
                foreach (var statement in method.Body.DescendantNodes().OfType<StatementSyntax>())
                {
                    if (!IsSequencePoint(statement, method.Node)) continue;
                    var line = tree.GetLineSpan(statement.Span).StartLinePosition.Line + 1;
                    var text = statement.ToString().Split('\n')[0].Trim();
                    if (text.Length > 160) text = text[..160];
                    var values = new List<string>();
                    if (apiArgument == "this.Api") values.Add("(\"this\", (object?)this)");
                    var flow = semantic.AnalyzeDataFlow(statement);
                    var assigned = flow?.DefinitelyAssignedOnEntry ?? default;
                    foreach (var symbol in semantic.LookupSymbols(statement.SpanStart).Take(96))
                    {
                        ITypeSymbol? type = symbol switch { IParameterSymbol p when p.RefKind == RefKind.None => p.Type, ILocalSymbol l when !assigned.IsDefault && assigned.Contains(l) && l.RefKind == RefKind.None => l.Type, _ => null };
                        if (type is null || type.IsRefLikeType || type.TypeKind is TypeKind.Pointer or TypeKind.FunctionPointer || symbol.Name.StartsWith("__alcyoneTrace")) continue;
                        values.Add($"({Quote(symbol.Name)}, (object?)@{symbol.Name})");
                    }
                    var statementCheckpoint = statement.Ancestors().Any(n => n is LockStatementSyntax or FixedStatementSyntax) ? "global::StateMachine.Scripting.ScriptTrace.Checkpoint();" : checkpoint;
                    var embedded = statement.Parent is not (BlockSyntax or SwitchSectionSyntax);
                    if (embedded) edits.Add((statement.Span.End, "\n}"));
                    edits.Add((statement.SpanStart, $"{(embedded ? "{" : "")}\n#line hidden\nif (global::StateMachine.Scripting.ScriptTrace.Step({line}, {Quote(text)})) global::StateMachine.Scripting.ScriptTrace.Values({string.Join(",", values)});\n{statementCheckpoint}\n#line {line} {Quote(tree.FilePath)}\n"));
                }
                edits.Add((method.Body.CloseBraceToken.SpanStart, $"\n#line hidden\n}} catch (global::System.Exception {error}) {{ {scope}.Fail({error}); {exceptionCall} throw; }}\n#line {tree.GetLineSpan(method.Body.CloseBraceToken.Span).StartLinePosition.Line + 1} {Quote(tree.FilePath)}\n"));
            }
            if (edits.Count == 0 && replacements.Count == 0) continue;
            var source = new StringBuilder(tree.GetText().ToString());
            foreach (var edit in edits.Select(e => (e.At, Length: 0, e.Text)).Concat(replacements).OrderByDescending(e => e.At))
            { source.Remove(edit.At, edit.Length); source.Insert(edit.At, edit.Text); }
            var updated = CSharpSyntaxTree.ParseText(source.ToString(), (CSharpParseOptions)tree.Options, tree.FilePath, Encoding.UTF8);
            compilation = compilation.ReplaceSyntaxTree(tree, updated);
        }
        return compilation;
    }
}
