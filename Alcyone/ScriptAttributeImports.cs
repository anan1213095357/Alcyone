using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace StateMachine.Scripting;

internal static class ScriptAttributeImports
{
    public static string[] Resolve(CSharpCompilation compilation, IEnumerable<string> packageReferences)
    {
        var paths = packageReferences.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var assemblies = compilation.References
            .Where(reference => reference.Display is not null && paths.Contains(reference.Display))
            .Select(compilation.GetAssemblyOrModuleSymbol).OfType<IAssemblySymbol>().ToArray();
        var aliases = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var diagnostic in compilation.GetDiagnostics().Where(d => d.Id == "CS0246" && d.Location.SourceTree is not null))
        {
            var node = diagnostic.Location.SourceTree!.GetRoot().FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true);
            var attribute = node.AncestorsAndSelf().OfType<AttributeSyntax>().FirstOrDefault();
            if (attribute?.Name is not IdentifierNameSyntax name) continue;
            var identifier = name.Identifier.ValueText;
            var typeName = identifier.EndsWith("Attribute", StringComparison.Ordinal) ? identifier : identifier + "Attribute";
            var matches = assemblies.SelectMany(a => Find(a.GlobalNamespace, typeName))
                .Where(t => t.DeclaredAccessibility == Accessibility.Public && t.Arity == 0 && IsAttribute(t))
                .Select(t => t.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)).Distinct().Take(2).ToArray();
            // Never guess between different libraries defining the same attribute.
            if (matches.Length == 1) aliases[identifier] = matches[0];
        }
        return aliases.Select(pair => $"global using @{pair.Key} = {pair.Value};").ToArray();
    }

    private static IEnumerable<INamedTypeSymbol> Find(INamespaceSymbol space, string name)
    {
        foreach (var type in space.GetTypeMembers(name)) yield return type;
        foreach (var child in space.GetNamespaceMembers())
            foreach (var type in Find(child, name)) yield return type;
    }

    private static bool IsAttribute(INamedTypeSymbol type)
    {
        for (var parent = type.BaseType; parent is not null; parent = parent.BaseType)
            if (parent.ToDisplayString() == "System.Attribute") return true;
        return false;
    }
}
