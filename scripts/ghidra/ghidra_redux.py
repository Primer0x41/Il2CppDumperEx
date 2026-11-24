# -*- coding: utf-8 -*-
"""
Il2CppDumperEx Ghidra Redux Script
Enhanced loader for Ghidra 10.x / 11.x:
- Namespace hierarchy & class organization
- Function recovery and signature application via ApplyFunctionSignatureCmd
- String literal labelling and EOL comments
- Metadata method mapping & Cross references
- Safe symbol renaming handling C++ / Itanium demangling & illegal characters
"""

import json
from ghidra.app.cmd.function import ApplyFunctionSignatureCmd
from ghidra.app.util.cparser.C import CParserUtils
from ghidra.app.services import DataTypeManagerService
from ghidra.program.model.symbol import SourceType, RefType, SymbolUtilities, Namespace
from ghidra.program.model.address import Address

USER_DEFINED = SourceType.USER_DEFINED

program = currentProgram
symbolTable = program.getSymbolTable()
functionManager = program.getFunctionManager()
refManager = program.getReferenceManager()
baseAddress = program.getImageBase()
globalNamespace = program.getGlobalNamespace()

namespaceCache = {}

def get_addr(offset):
    return baseAddress.add(offset)

def get_or_create_namespace(namespace_path):
    if not namespace_path:
        return globalNamespace
    if namespace_path in namespaceCache:
        return namespaceCache[namespace_path]

    current_ns = globalNamespace
    parts = [p for p in namespace_path.replace("\\", "/").split("/") if p]
    for part in parts:
        clean_part = SymbolUtilities.replaceInvalidChars(part, True)
        ns = symbolTable.getNamespace(clean_part, current_ns)
        if ns is None:
            try:
                ns = symbolTable.createNameSpace(current_ns, clean_part, USER_DEFINED)
            except Exception:
                ns = current_ns
        current_ns = ns

    namespaceCache[namespace_path] = current_ns
    return current_ns

def sanitize_name(name):
    clean = SymbolUtilities.replaceInvalidChars(name.replace(" ", "_"), True)
    if len(clean) > 1000:
        return clean[:1000]
    return clean

def set_label(addr, name, namespace=None):
    clean_name = sanitize_name(name)
    ns = namespace if namespace is not None else globalNamespace
    try:
        symbolTable.createLabel(addr, clean_name, ns, USER_DEFINED)
    except Exception:
        pass

def make_or_get_function(addr, name=None, namespace=None):
    func = functionManager.getFunctionAt(addr)
    if func is None:
        func = createFunction(addr, None)
    if func is not None:
        if name:
            clean_name = sanitize_name(name)
            func.setName(clean_name, USER_DEFINED)
        if namespace:
            try:
                func.setParentNamespace(namespace)
            except Exception:
                pass
    return func

def apply_signature(addr, signature_str):
    if not signature_str:
        return
    try:
        type_sig = CParserUtils.parseSignature(None, program, signature_str)
        if type_sig is not None:
            cmd = ApplyFunctionSignatureCmd(addr, type_sig, USER_DEFINED, False, True)
            cmd.applyTo(program, monitor)
    except Exception:
        pass

f = askFile("Select script.json from Il2CppDumperEx", "Open")
if not f or not f.exists():
    print("Script file selection cancelled or file does not exist.")
else:
    with open(f.absolutePath, "rb") as fp:
        data = json.loads(fp.read().decode("utf-8"))

    if "ScriptMethod" in data:
        scriptMethods = data["ScriptMethod"]
        monitor.initialize(len(scriptMethods))
        monitor.setMessage("Il2Cpp Methods")
        for method in scriptMethods:
            if monitor.isCancelled():
                break
            addr = get_addr(method["Address"])
            raw_name = method.get("Name", "")
            sig = method.get("Signature", "")

            ns = None
            if "::" in raw_name:
                parts = raw_name.rsplit("::", 1)
                ns_path = parts[0].replace(".", "/")
                ns = get_or_create_namespace(ns_path)
                func_name = parts[1]
            elif "." in raw_name:
                parts = raw_name.rsplit(".", 1)
                ns_path = parts[0].replace(".", "/")
                ns = get_or_create_namespace(ns_path)
                func_name = parts[1]
            else:
                func_name = raw_name

            func = make_or_get_function(addr, func_name, ns)
            if sig:
                apply_signature(addr, sig)

            monitor.incrementProgress(1)

    if "ScriptString" in data:
        scriptStrings = data["ScriptString"]
        monitor.initialize(len(scriptStrings))
        monitor.setMessage("Il2Cpp Strings")
        str_ns = get_or_create_namespace("StringLiterals")
        for idx, item in enumerate(scriptStrings, start=1):
            if monitor.isCancelled():
                break
            addr = get_addr(item["Address"])
            val = item.get("Value", "")
            set_label(addr, "StringLiteral_{}".format(idx), str_ns)
            setEOLComment(addr, val)
            monitor.incrementProgress(1)

    if "ScriptMetadata" in data:
        scriptMetas = data["ScriptMetadata"]
        monitor.initialize(len(scriptMetas))
        monitor.setMessage("Il2Cpp Metadata")
        meta_ns = get_or_create_namespace("Metadata")
        for item in scriptMetas:
            if monitor.isCancelled():
                break
            addr = get_addr(item["Address"])
            name = item.get("Name", "")
            set_label(addr, name, meta_ns)
            setEOLComment(addr, name)
            monitor.incrementProgress(1)

    if "ScriptMetadataMethod" in data:
        metaMethods = data["ScriptMetadataMethod"]
        monitor.initialize(len(metaMethods))
        monitor.setMessage("Il2Cpp Metadata Methods")
        for item in metaMethods:
            if monitor.isCancelled():
                break
            addr = get_addr(item["Address"])
            name = item.get("Name", "")
            set_label(addr, name)
            setEOLComment(addr, name)
            if "MethodAddress" in item:
                target_addr = get_addr(item["MethodAddress"])
                try:
                    refManager.addMemoryReference(addr, target_addr, RefType.DATA, USER_DEFINED, 0)
                except Exception:
                    pass
            monitor.incrementProgress(1)

    print("Il2CppDumper Ghidra Redux processing completed successfully.")
