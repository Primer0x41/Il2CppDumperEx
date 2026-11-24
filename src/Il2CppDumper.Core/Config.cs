namespace Il2CppDumper
{
    public enum DecompileSplitMode
    {
        None,
        Assembly,
        Namespace
    }

    public class Config
    {
        public bool DumpCs { get; set; } = true;
        public bool DumpMethod { get; set; } = true;
        public bool DumpField { get; set; } = true;
        public bool DumpProperty { get; set; } = true;
        public bool DumpAttribute { get; set; } = true;
        public bool DumpFieldOffset { get; set; } = true;
        public bool DumpMethodOffset { get; set; } = true;
        public bool DumpTypeDefIndex { get; set; } = true;
        public bool GenerateDummyDll { get; set; } = true;
        public bool GenerateStruct { get; set; } = true;
        public bool DummyDllAddToken { get; set; } = true;
        public bool RequireAnyKey { get; set; } = false;
        public bool ForceIl2CppVersion { get; set; } = false;
        public double ForceVersion { get; set; } = 24.3;
        public bool ForceDump { get; set; } = false;
        public bool NoRedirectedPointer { get; set; } = false;
        public DecompileSplitMode SplitMode { get; set; } = DecompileSplitMode.None;
        public bool GenerateUnifiedJson { get; set; } = false;
        public bool GenerateModProject { get; set; } = false;
    }
}
