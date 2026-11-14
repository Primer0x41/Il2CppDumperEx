# -*- coding: utf-8 -*-
"""
Modern IDA Pro loader script with ida_dirtree folder organization
and ida_srclang clang type parsing support (IDA 7.7+ / 8.x / 9.x).
"""
import json
import ida_funcs
import ida_idaapi
import ida_name
import ida_pro

IDACLANG_AVAILABLE = False
FOLDERS_AVAILABLE = False

if ida_pro.IDA_SDK_VERSION >= 770:
    try:
        import ida_dirtree
        import ida_srclang
        IDACLANG_AVAILABLE = True
        FOLDERS_AVAILABLE = True
        print("[+] IDA Clang parser and dirtree namespace folders available.")
    except ImportError:
        pass

imageBase = ida_idaapi.get_imagebase()

def get_addr(addr):
    return imageBase + addr

def set_name(addr, name):
    ret = ida_name.set_name(addr, name, ida_name.SN_NOWARN | ida_name.SN_NOCHECK)
    if ret == 0:
        new_name = f"{name}_{addr:X}"
        ida_name.set_name(addr, new_name, ida_name.SN_NOWARN | ida_name.SN_NOCHECK)

def make_function(start, end):
    next_func = ida_funcs.get_next_func(start)
    if next_func and next_func.start_ea < end:
        end = next_func.start_ea
    if ida_funcs.get_func(start):
        ida_funcs.del_func(start)
    ida_funcs.add_func(start, end)

def organize_dirtree(scriptMethods):
    if not FOLDERS_AVAILABLE:
        return
    try:
        dirtree = ida_dirtree.get_std_dirtree(ida_dirtree.DIRTREE_FUNCS)
        for method in scriptMethods:
            name = method.get("Name", "")
            if "$$" in name or "." in name:
                parts = name.replace("$$", ".").split(".")
                if len(parts) > 1:
                    folder_path = "/" + "/".join(parts[:-1])
                    dirtree.mkdir(folder_path)
                    ea = get_addr(method["Address"])
                    dirtree.link(folder_path + "/" + parts[-1], ea)
    except Exception as e:
        print(f"[*] Dirtree folder organization skipped: {e}")

def main():
    path = ida_idaapi.ask_file(False, "*.json", "Select script.json from Il2CppDumperEx")
    if not path:
        return

    hpath = ida_idaapi.ask_file(False, "*.h", "Select il2cpp.h (optional, cancel to skip)")

    if hpath:
        with open(hpath, "r", encoding="utf-8", errors="ignore") as f:
            header_content = f.read()

        if IDACLANG_AVAILABLE:
            try:
                ida_srclang.set_parser_type("clang")
                ida_srclang.parse_decls(header_content, 0)
                print("[+] Parsed declarations using IDACLANG parser.")
            except Exception:
                import idc
                idc.parse_decls(header_content, 0)
        else:
            import idc
            idc.parse_decls(header_content, 0)

    with open(path, "r", encoding="utf-8") as f:
        data = json.load(f)

    if "Addresses" in data:
        addresses = data["Addresses"]
        for index in range(len(addresses) - 1):
            start = get_addr(addresses[index])
            end = get_addr(addresses[index + 1])
            make_function(start, end)

    if "ScriptMethod" in data:
        scriptMethods = data["ScriptMethod"]
        for scriptMethod in scriptMethods:
            addr = get_addr(scriptMethod["Address"])
            name = scriptMethod["Name"]
            set_name(addr, name)

        organize_dirtree(scriptMethods)

    if "ScriptString" in data:
        for scriptString in data["ScriptString"]:
            addr = get_addr(scriptString["Address"])
            value = scriptString.get("Value", "")
            set_name(addr, f"str_{value[:24]}")

    print("[+] Il2CppDumperEx IDA Redux script import completed successfully.")

if __name__ == "__main__":
    main()
