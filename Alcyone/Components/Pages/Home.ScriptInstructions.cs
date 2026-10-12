namespace StateMachine.Components.Pages;

public partial class Home
{
    private const string ScriptDevelopmentInstructions = """

统一 C# 热重载开发规则：生成完整可编译的 public sealed class，继承 StateScript。
StateScript 提供 protected Api、Vars、Log(message,type="SCRIPT",level="info")、Service<T>()。
Api 提供 CancellationToken、Delay(milliseconds)、Input、Recognition、GetNode(name)、SetNode(name,value)。
Vars 提供 Get<T>(name,defaultValue)、Set(name,value)。禁止编造 Api 的方法或服务。
条件用 [StateCondition("名称","分类")] 标注 public 无参数 bool/Task<bool>/ValueTask<bool> 方法；true 表示完成，异步任务必须 await 完成再返回。
动作用 [StateAction("名称","分类")] 标注 public 方法。C# 条件/动作宿主使用命名空间.类名.方法名绑定，不是脚本文件名。
外部库用顶部 #r "nuget: 包名, 版本"；自动恢复所有依赖，带齐 using，禁止要求用户手动安装。
FreeSql 实体属性属于 FreeSql.DataAnnotations，SQLite 使用 FreeSql.Provider.Sqlite。库对象使用完要释放。
初始化数据库的条件应幂等：创建目录、同步实体表、验证实际表存在后才返回 true；不要重复插入或吞异常伪装成功。
条件会每周期调用，避免 UI 阻塞：数据库或文件工作使用 async Task<bool> 并通过 Task.Run 等执行阻塞操作，检查 Api.CancellationToken；不要使用 async void 或后台任务未完成就返回 true。
已有脚本优先保留其他代码，不要重定义同名类型。仅信息不足且无法从上下文确认时才询问。
""";
}
