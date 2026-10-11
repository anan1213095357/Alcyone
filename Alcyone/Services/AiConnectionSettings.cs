using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace StateMachine;
public sealed class AiConnectionSettings
{
    private readonly string _path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Alcyone", "ai.json");
    public string Endpoint { get; private set; }
    public string ApiKey { get; private set; }
    public string Model { get; private set; }
    public AiConnectionSettings(IConfiguration configuration)
    {
        Endpoint = NormalizeEndpoint(configuration["AI:Endpoint"] ?? "https://dashscope.aliyuncs.com/compatible-mode/v1/chat/completions");
        ApiKey = configuration["AI:ApiKey"] ?? Environment.GetEnvironmentVariable("DASHSCOPE_API_KEY") ?? "";
        Model = configuration["AI:Model"] ?? "qwen3.8-max";
        try
        {
            if(File.Exists(_path) && JsonSerializer.Deserialize<Saved>(File.ReadAllText(_path)) is { Key: not null } saved
                && !string.IsNullOrWhiteSpace(saved.Endpoint) && !string.IsNullOrWhiteSpace(saved.Model))
            {
                var key = Protect(saved.Key,false);
                Endpoint=NormalizeEndpoint(saved.Endpoint);Model=saved.Model;ApiKey=key;
            }
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or JsonException or FormatException or InvalidOperationException or System.ComponentModel.Win32Exception){ }
    }
    public void Save(string endpoint, string key, string model)
    {
        endpoint=NormalizeEndpoint(endpoint);model=model.Trim();
        if(string.IsNullOrWhiteSpace(model))throw new InvalidOperationException("请填写模型名。");
        var saved=new Saved(endpoint,Protect(key.Trim(),true),model);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path+".tmp",JsonSerializer.Serialize(saved));File.Move(_path+".tmp",_path,true);
        Endpoint=endpoint;ApiKey=key.Trim();Model=model;
    }
    public static string NormalizeEndpoint(string endpoint)
    {
        if(!Uri.TryCreate(endpoint.Trim(),UriKind.Absolute,out var uri) || uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo))
            throw new InvalidOperationException("请输入有效的 HTTP / HTTPS 接口地址。");
        var builder = new UriBuilder(uri);
        var path = uri.AbsolutePath.TrimEnd('/');
        if (path.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)) path += "/chat/completions";
        else if (path.Length == 0) path = "/v1/chat/completions";
        builder.Path = path;
        return builder.Uri.AbsoluteUri;
    }
    private sealed record Saved(string Endpoint,string Key,string Model);
    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Length; public nint Data; }
    private static string Protect(string value,bool encrypt)
    {
        if(value.Length==0)return "";
        var bytes=encrypt?Encoding.UTF8.GetBytes(value):Convert.FromBase64String(value);
        var input=new Blob{Length=bytes.Length,Data=Marshal.AllocHGlobal(bytes.Length)};Blob output=default;
        try
        {
            Marshal.Copy(bytes,0,input.Data,bytes.Length);
            var success=encrypt?CryptProtectData(ref input, null, 0, 0, 0, 1, out output):CryptUnprotectData(ref input, 0, 0, 0, 0, 1, out output);
            if(!success)throw new System.ComponentModel.Win32Exception();
            var result=new byte[output.Length];Marshal.Copy(output.Data,result,0,result.Length);
            return encrypt?Convert.ToBase64String(result):Encoding.UTF8.GetString(result);
        }
        finally { Marshal.FreeHGlobal(input.Data);if(output.Data!=0)LocalFree(output.Data); }
    }
    [DllImport("crypt32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern bool CryptProtectData(ref Blob input,string? description,nint entropy,nint reserved,nint prompt,uint flags,out Blob output);
    [DllImport("crypt32.dll",SetLastError=true)]private static extern bool CryptUnprotectData(ref Blob input,nint description,nint entropy,nint reserved,nint prompt,uint flags,out Blob output);
    [DllImport("kernel32.dll")]private static extern nint LocalFree(nint pointer);
}
