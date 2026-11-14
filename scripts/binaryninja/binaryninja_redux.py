# -*- coding: utf-8 -*-
"""
MetaDumper / Il2CppDumper Binary Ninja Redux Script
Enhanced loader for Binary Ninja 3.x / 4.x:
- Component folder hierarchy matching C# namespaces
- Automatic type parsing from il2cpp.h using parse_types_from_string
- Function type signatures applied via parse_type_string
- Background task thread with granular progress reporting
- String literal symbol naming and comments
"""

import json
from os.path import exists, dirname, join
from binaryninja import (
    BinaryView,
    BackgroundTaskThread,
    Component,
    Type,
    log_info,
    log_warn,
    log_error,
    PluginCommand,
)

def get_addr(bv: BinaryView, addr: int) -> int:
    return bv.start + addr

class Il2CppReduxTask(BackgroundTaskThread):
    def __init__(self, bv: BinaryView, script_path: str, header_path: str):
        super().__init__("MetaDumper Redux: Processing Il2Cpp metadata...", True)
        self.bv = bv
        self.script_path = script_path
        self.header_path = header_path
        self.has_types = False
        self._components = {}

    def get_or_create_component(self, namespace_path: str) -> Component:
        if not namespace_path:
            return None
        if namespace_path in self._components:
            return self._components[namespace_path]

        current_comp = None
        parts = [p for p in namespace_path.replace("\\", "/").split("/") if p]
        accum = ""
        for part in parts:
            accum = f"{accum}/{part}" if accum else part
            if accum in self._components:
                current_comp = self._components[accum]
            else:
                current_comp = self.bv.create_component(part, current_comp)
                self._components[accum] = current_comp
        return current_comp

    def process_header(self):
        if not exists(self.header_path):
            log_warn(f"Header file not found: {self.header_path}")
            return
        self.progress = "MetaDumper: Parsing C header types (1/3)..."
        try:
            with open(self.header_path, "r", encoding="utf-8", errors="replace") as f:
                header_text = f.read()
            result = self.bv.parse_types_from_string(header_text)
            if result and hasattr(result, "types"):
                total = len(result.types)
                for idx, (name, typ) in enumerate(result.types.items()):
                    if self.cancelled:
                        return
                    if idx % 200 == 0 and total > 0:
                        self.progress = f"MetaDumper: Registering types {idx}/{total} ({idx*100/total:.1f}%)..."
                    if not self.bv.get_type_by_name(name):
                        self.bv.define_user_type(name, typ)
                self.has_types = True
                log_info(f"Imported {total} types from header.")
        except Exception as e:
            log_error(f"Error parsing C header: {e}")

    def process_methods(self, data: dict):
        script_methods = data.get("ScriptMethod", [])
        total = len(script_methods)
        self.progress = "MetaDumper: Processing methods (2/3)..."

        for idx, method in enumerate(script_methods):
            if self.cancelled:
                return
            if idx % 200 == 0 and total > 0:
                self.progress = f"MetaDumper: Applying methods {idx}/{total} ({idx*100/total:.1f}%)..."

            raw_addr = method.get("Address", 0)
            addr = get_addr(self.bv, raw_addr)
            raw_name = method.get("Name", "")
            sig = method.get("Signature", "")

            # Component namespace extraction
            ns_path = ""
            func_name = raw_name
            if "::" in raw_name:
                parts = raw_name.rsplit("::", 1)
                ns_path = parts[0].replace(".", "/")
                func_name = parts[1]
            elif "." in raw_name:
                parts = raw_name.rsplit(".", 1)
                ns_path = parts[0].replace(".", "/")
                func_name = parts[1]

            func = self.bv.get_function_at(addr)
            if func is None:
                self.bv.create_user_function(addr)
                func = self.bv.get_function_at(addr)

            if func is not None:
                func.name = func_name.replace("$", "_").replace(".", "_")

                if ns_path:
                    comp = self.get_or_create_component(ns_path)
                    if comp:
                        comp.add_function(func)

                if sig and self.has_types:
                    try:
                        parsed_sig, _ = self.bv.parse_type_string(sig)
                        if parsed_sig is not None:
                            func.function_type = parsed_sig
                    except Exception:
                        pass

    def process_strings(self, data: dict):
        script_strings = data.get("ScriptString", [])
        total = len(script_strings)
        self.progress = "MetaDumper: Processing strings (3/3)..."

        str_comp = self.get_or_create_component("Strings")
        for idx, item in enumerate(script_strings, start=1):
            if self.cancelled:
                return
            if idx % 500 == 0 and total > 0:
                self.progress = f"MetaDumper: Processing strings {idx}/{total} ({idx*100/total:.1f}%)..."

            addr = get_addr(self.bv, item.get("Address", 0))
            val = item.get("Value", "")

            var = self.bv.get_data_var_at(addr)
            if var is not None:
                var.name = f"StringLiteral_{idx}"
                if str_comp:
                    str_comp.add_data_variable(var)
            self.bv.set_comment_at(addr, val)

    def run(self):
        log_info("MetaDumper Redux: Starting Binary Ninja import task...")
        self.process_header()
        if not exists(self.script_path):
            log_error(f"script.json not found: {self.script_path}")
            return

        with open(self.script_path, "r", encoding="utf-8", errors="replace") as f:
            data = json.load(f)

        self.process_methods(data)
        self.process_strings(data)
        self.bv.update_analysis()
        log_info("MetaDumper Redux: Successfully finished import!")

def run_redux_script(bv: BinaryView):
    from binaryninja.interaction import OpenFileNameField, get_form_input
    f_script = OpenFileNameField("script.json file")
    f_header = OpenFileNameField("il2cpp.h file (optional)")
    if not get_form_input([f_script, f_header], "MetaDumper / Il2CppDumper Redux"):
        return
    if not f_script.result:
        return

    task = Il2CppReduxTask(bv, f_script.result, f_header.result or "")
    task.start()

PluginCommand.register("MetaDumper\\Import script.json & il2cpp.h (Redux)", "Loads Il2Cpp symbols and organizes functions into component folders", run_redux_script)
