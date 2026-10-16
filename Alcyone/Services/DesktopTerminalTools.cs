using System.Text.RegularExpressions;

namespace StateMachine;

// Inspection tools use fixed PowerShell templates. Model text is always quoted as data.
public static class DesktopTerminalTools
{
    public static string Title(string name) => name switch
    {
        "installed_apps" => "查询已安装软件", "shortcuts" => "查询快捷方式", "search_executables" => "查找程序文件",
        "inspect_file" => "核实文件信息", "list_directory" => "查看目录", "processes" => "查询进程", _ => "查询"
    };
    private static string Quote(string text) => "'" + text.Replace("'", "''") + "'";
    public static string Build(string name, string[]? terms, string[]? paths, string? path)
    {
        terms ??= [];
        if (terms.Length > 12 || terms.Any(t => t is { Length: > 100 })) throw new ArgumentException("搜索词过长或过多。");
        var patterns = terms.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t =>
        {
            t = t.Trim();
            return t.Length <= 5 ? string.Join(".*", t.Select(c => c switch
            {
                '1' or 'i' or 'I' or 'l' or 'L' => "[1il]",
                '0' or 'o' or 'O' => "[0o]",
                _ => Regex.Escape(c.ToString())
            })) : Regex.Escape(t);
        }).ToArray();
        var pattern = Quote(patterns.Length == 0 ? ".*" : string.Join("|", patterns));
        string LocalPath(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > 1000 || !Path.IsPathFullyQualified(value) || value.StartsWith(@"\\", StringComparison.Ordinal))
                throw new ArgumentException("请提供本机的完整路径。");
            return Quote(Path.GetFullPath(value));
        }
        if (paths is { Length: > 6 }) throw new ArgumentException("一次最多查询6个目录。");
        var roots = paths is { Length: > 0 }
            ? "@(" + string.Join(",", paths.Select(LocalPath)) + ")"
            : "@($env:ProgramFiles, ${env:ProgramFiles(x86)}, (Join-Path $env:LOCALAPPDATA 'Programs')) | Where-Object { $_ }";
        var body = name switch
        {
            "installed_apps" => """
                $keys = @('HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*', 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*', 'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*')
                $items = @(Get-ItemProperty -Path $keys -ErrorAction SilentlyContinue | Where-Object { $_.DisplayName -and ($_.DisplayName + ' ' + $_.Publisher + ' ' + $_.InstallLocation) -match PATTERN } | Select-Object @{n='Name';e={$_.DisplayName}}, @{n='Version';e={$_.DisplayVersion}}, Publisher, InstallLocation, DisplayIcon | Sort-Object Name -Unique)
                """.Replace("PATTERN", pattern),
            "shortcuts" => """
                $shell = New-Object -ComObject WScript.Shell
                try {
                    $roots = @([Environment]::GetFolderPath('StartMenu'), [Environment]::GetFolderPath('CommonStartMenu'), [Environment]::GetFolderPath('DesktopDirectory'), [Environment]::GetFolderPath('CommonDesktopDirectory')) | Select-Object -Unique
                    $items = @(foreach ($root in $roots) {
                        Get-ChildItem -LiteralPath $root -Filter '*.lnk' -File -Depth 4 -ErrorAction SilentlyContinue | ForEach-Object {
                            $link = $shell.CreateShortcut($_.FullName)
                            if (($_.BaseName + ' ' + $link.TargetPath + ' ' + $link.Description) -match PATTERN) {
                                [pscustomobject]@{Name=$_.BaseName; Target=$link.TargetPath; Description=$link.Description; Shortcut=$_.FullName}
                            }
                        }
                    })
                } finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) }
                """.Replace("PATTERN", pattern),
            "search_executables" => Regex.Replace("""
                $roots = ROOTS
                $items = @(foreach ($root in ($roots | Select-Object -Unique)) {
                    Get-ChildItem -LiteralPath $root -Filter '*.exe' -File -Depth 3 -ErrorAction SilentlyContinue | Where-Object { ($_.BaseName + ' ' + $_.Directory.Name) -match PATTERN } | Select-Object Name, FullName, Length
                })
                """, "PATTERN|ROOTS", m => m.Value == "PATTERN" ? pattern : roots),
            "inspect_file" => """
                $file = Get-Item -LiteralPath FILEPATH -ErrorAction Stop
                $items = @([pscustomobject]@{Name=$file.Name; FullName=$file.FullName; Length=$file.Length; IsDirectory=$file.PSIsContainer; Product=$file.VersionInfo.ProductName; Description=$file.VersionInfo.FileDescription; Company=$file.VersionInfo.CompanyName; Version=$file.VersionInfo.ProductVersion})
                """.Replace("FILEPATH", LocalPath(path ?? "")),
            "list_directory" => "$items = @(Get-ChildItem -LiteralPath " + LocalPath(path ?? "") + " -ErrorAction Stop | Select-Object Name, FullName, Length, PSIsContainer)",
            "processes" => "$items = @(Get-Process -ErrorAction SilentlyContinue | Where-Object { ($_.ProcessName + ' ' + $_.Path) -match " + pattern + " } | Select-Object ProcessName, Id, Path)",
            _ => throw new ArgumentException("不支持的查询工具：" + name)
        };
        return "& {\n$ErrorActionPreference = 'Stop'\n" + body + "\n[pscustomobject]@{Count=$items.Count; Truncated=($items.Count -gt 350); Items=@($items | Select-Object -First 350)} | ConvertTo-Json -Depth 5 -Compress\n}";
    }
}
