#nullable enable
using System;
using System.IO;
using System.Text;

namespace Il2CppDumper
{
    public static class ModProjectGenerator
    {
        public static void Generate(string outputDir, Config config, string? projectName = null)
        {
            string modDir = Path.Combine(outputDir, "ModProject");
            Directory.CreateDirectory(modDir);

            string safeName = SanitizeIdentifier(projectName ?? "GameMod");
            string csprojPath = Path.Combine(modDir, $"{safeName}.csproj");
            string pluginCsPath = Path.Combine(modDir, "Plugin.cs");

            string csprojContent = $@"<Project Sdk=""Microsoft.NET.Sdk"">

  <PropertyGroup>
    <TargetFramework>netstandard2.1</TargetFramework>
    <LangVersion>latest</LangVersion>
    <Nullable>enable</Nullable>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
    <AssemblyName>{safeName}</AssemblyName>
    <RootNamespace>{safeName}</RootNamespace>
  </PropertyGroup>

  <!-- Reference exported DummyDlls -->
  <ItemGroup>
    <Reference Include=""../DummyDll/*.dll"">
      <Private>false</Private>
    </Reference>
  </ItemGroup>

</Project>
";

            string pluginCsContent = $@"using System;

namespace {safeName}
{{
    public static class Plugin
    {{
        public const string ModName = ""{safeName}"";
        public const string ModVersion = ""1.0.0"";

        public static void Initialize()
        {{
            Console.WriteLine($""[{{ModName}}] Initialized successfully!"");
        }}
    }}
}}
";

            File.WriteAllText(csprojPath, csprojContent, Encoding.UTF8);
            File.WriteAllText(pluginCsPath, pluginCsContent, Encoding.UTF8);
        }

        private static string SanitizeIdentifier(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return "GameMod";

            var sb = new StringBuilder(name.Length);
            bool first = true;
            foreach (char c in name)
            {
                if (first)
                {
                    if (char.IsLetter(c) || c == '_')
                    {
                        sb.Append(c);
                        first = false;
                    }
                    else if (char.IsDigit(c))
                    {
                        sb.Append('_');
                        sb.Append(c);
                        first = false;
                    }
                }
                else
                {
                    if (char.IsLetterOrDigit(c) || c == '_')
                    {
                        sb.Append(c);
                    }
                    else
                    {
                        sb.Append('_');
                    }
                }
            }

            string result = sb.ToString();
            if (result.StartsWith('_') && result.Length > 1 && char.IsLetterOrDigit(result[1]))
            {
                // Preserve leading underscore for names starting with a digit
            }
            else
            {
                result = result.TrimStart('_');
            }
            result = result.TrimEnd('_');
            return string.IsNullOrEmpty(result) ? "GameMod" : result;
        }
    }
}
