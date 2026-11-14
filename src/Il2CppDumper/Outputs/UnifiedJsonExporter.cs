using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Il2CppDumper
{
    public class UnifiedMetadataModel
    {
        public AddressMapModel AddressMap { get; set; } = new();
    }

    public class AddressMapModel
    {
        public List<UnifiedMethodModel> Methods { get; set; } = new();
        public List<UnifiedStringLiteralModel> StringLiterals { get; set; } = new();
        public List<UnifiedMetadataModelItem> MetadataItems { get; set; } = new();
    }

    public class UnifiedMethodModel
    {
        public string Address { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Signature { get; set; } = string.Empty;
        public string Group { get; set; }
    }

    public class UnifiedStringLiteralModel
    {
        public string Address { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
        public int Length { get; set; }
    }

    public class UnifiedMetadataModelItem
    {
        public string Address { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
    }

    public static class UnifiedJsonExporter
    {
        public static void Export(ScriptJson scriptJson, string outputDir)
        {
            var model = new UnifiedMetadataModel();

            if (scriptJson.ScriptMethod != null)
            {
                foreach (var method in scriptJson.ScriptMethod)
                {
                    string group = null;
                    if (method.Name.Contains("::"))
                    {
                        var parts = method.Name.Split(new[] { "::" }, StringSplitOptions.None);
                        group = parts[0].Replace('.', '/');
                    }

                    model.AddressMap.Methods.Add(new UnifiedMethodModel
                    {
                        Address = $"0x{method.Address:X}",
                        Name = method.Name,
                        Signature = method.Signature ?? string.Empty,
                        Group = group
                    });
                }
            }

            if (scriptJson.ScriptString != null)
            {
                foreach (var str in scriptJson.ScriptString)
                {
                    model.AddressMap.StringLiterals.Add(new UnifiedStringLiteralModel
                    {
                        Address = $"0x{str.Address:X}",
                        Value = str.Value ?? string.Empty,
                        Length = str.Value?.Length ?? 0
                    });
                }
            }

            if (scriptJson.ScriptMetadata != null)
            {
                foreach (var meta in scriptJson.ScriptMetadata)
                {
                    model.AddressMap.MetadataItems.Add(new UnifiedMetadataModelItem
                    {
                        Address = $"0x{meta.Address:X}",
                        Name = meta.Name ?? string.Empty
                    });
                }
            }

            string outPath = Path.Combine(outputDir, "metadata.json");
            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };
            File.WriteAllText(outPath, JsonSerializer.Serialize(model, options));
        }
    }
}
