using System;
using System.IO;
using System.Text.Json;

namespace Il2CppDumper
{
    /// <summary>
    /// 从 Il2CppInspectorRedux 的 script.json 读取地址信息
    /// 用于支持 v38+ 版本的自动检测
    /// </summary>
    public class Il2CppInspectorBridge
    {
        public static (ulong codeReg, ulong metadataReg)? TryReadAddresses(string scriptJsonPath)
        {
            try
            {
                if (!File.Exists(scriptJsonPath))
                    return null;

                var json = File.ReadAllText(scriptJsonPath);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                // 尝试读取地址
                ulong codeReg = 0;
                ulong metadataReg = 0;

                if (root.TryGetProperty("codeRegistration", out var codeRegElem))
                    codeReg = ParseAddress(codeRegElem);

                if (root.TryGetProperty("metadataRegistration", out var metaRegElem))
                    metadataReg = ParseAddress(metaRegElem);

                if (root.TryGetProperty("metadata", out var metadata))
                {
                    // 从 metadata 对象中读取
                    if (metadata.TryGetProperty("codeRegistration", out var cr))
                        codeReg = ParseAddress(cr);

                    if (metadata.TryGetProperty("metadataRegistration", out var mr))
                        metadataReg = ParseAddress(mr);
                }

                if (codeReg != 0 && metadataReg != 0)
                    return (codeReg, metadataReg);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Warning: Failed to read script.json: {ex.Message}");
            }

            return null;
        }

        public static ulong ParseAddress(JsonElement elem)
        {
            if (elem.ValueKind == JsonValueKind.Number && elem.TryGetUInt64(out var num))
                return num;
            if (elem.ValueKind == JsonValueKind.String)
            {
                string s = elem.GetString()?.Trim() ?? "";
                if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                    s = s[2..];
                if (ulong.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out var val))
                    return val;
                if (ulong.TryParse(s, System.Globalization.NumberStyles.Integer, null, out var decVal))
                    return decVal;
            }
            return 0;
        }

        /// <summary>
        /// 自动查找并使用 script.json
        /// </summary>
        public static (ulong codeReg, ulong metadataReg)? AutoDetect(string il2cppPath)
        {
            // 在 il2cpp 文件所在目录查找 script.json
            var dir = Path.GetDirectoryName(il2cppPath);
            if (string.IsNullOrEmpty(dir))
                dir = Directory.GetCurrentDirectory();

            var scriptJson = Path.Combine(dir, "script.json");
            if (File.Exists(scriptJson))
            {
                Console.WriteLine($"Found script.json from Il2CppInspectorRedux");
                var result = TryReadAddresses(scriptJson);
                if (result.HasValue)
                {
                    var (codeReg, metadataReg) = result.Value;
                    Console.WriteLine($"CodeRegistration: 0x{codeReg:X}");
                    Console.WriteLine($"MetadataRegistration: 0x{metadataReg:X}");
                    return result;
                }
            }

            return null;
        }
    }
}
