using System.IO;

namespace Il2CppDumper
{
    public abstract class ElfBase : Il2Cpp
    {
        public bool IsSElfContainer { get; set; }

        protected ElfBase(Stream stream) : base(stream) { }
        protected abstract void Load();
        protected abstract bool CheckSection();

        public override bool CheckDump() => !IsSElfContainer && !CheckSection();

        public void Reload() => Load();
    }
}
