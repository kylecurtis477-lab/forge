# tools/cpython/embed.fsi: hosted CPython embed API, written in Forge (Phase 11.5(a) step 2).
# Calls libpython's exported C API through the 11.2 SysV C ABI. No C shim: macros are
# avoided by using the function forms CPython also exports (PyRun_SimpleString,
# PyCMethod_New, PyModule_New, Py_IncRef / Py_DecRef, Py_InitializeFromConfig, …) and
# by using PyTuple_Size / PyTuple_GetItem instead of varargs PyArg_ParseTuple.
# PyConfig / PyStatus / PyMethodDef are transcribed from the pinned 3.13.5 headers
# (release build: not Py_DEBUG, Py_GIL_DISABLED, Py_STATS, or MS_WINDOWS). tests
# check sizeof(PyConfig)==448, offsetof(home)==296, sizeof(PyStatus)==32,
# sizeof(PyMethodDef)==32. Isolated init sets home to the install prefix.
# Link with gcc -no-pie against prefix/lib/libpython3.13.a and the box libc, not fld
# (fld stays no-libc and does not accept .a archives; fas objects are not PIC).
# A PyMethodDef passed to PyCMethod_New must outlive the method; add_method heap-
# allocates it with libc malloc.
# Forge-to-Python (11.5(a) step 3): forge_py_eval, forge_py_main_attr, and
# forge_py_call_i64 call PyRun_String / PyObject_GetAttrString / PyObject_Call*
# (function forms, no varargs).

struct PyWideStringList:
    length: i64
    items: nptr[u8]

struct PyConfig:
    _config_init: i32
    isolated: i32
    use_environment: i32
    dev_mode: i32
    install_signal_handlers: i32
    use_hash_seed: i32
    hash_seed: u64
    faulthandler: i32
    tracemalloc: i32
    perf_profiling: i32
    import_time: i32
    code_debug_ranges: i32
    show_ref_count: i32
    dump_refs: i32
    dump_refs_file: nptr[u8]
    malloc_stats: i32
    filesystem_encoding: nptr[u8]
    filesystem_errors: nptr[u8]
    pycache_prefix: nptr[u8]
    parse_argv: i32
    orig_argv: PyWideStringList
    argv: PyWideStringList
    xoptions: PyWideStringList
    warnoptions: PyWideStringList
    site_import: i32
    bytes_warning: i32
    warn_default_encoding: i32
    inspect: i32
    interactive: i32
    optimization_level: i32
    parser_debug: i32
    write_bytecode: i32
    verbose: i32
    quiet: i32
    user_site_directory: i32
    configure_c_stdio: i32
    buffered_stdio: i32
    stdio_encoding: nptr[u8]
    stdio_errors: nptr[u8]
    check_hash_pycs_mode: nptr[u8]
    use_frozen_modules: i32
    safe_path: i32
    int_max_str_digits: i32
    cpu_count: i32
    pathconfig_warnings: i32
    program_name: nptr[u8]
    pythonpath_env: nptr[u8]
    home: nptr[u8]
    platlibdir: nptr[u8]
    module_search_paths_set: i32
    module_search_paths: PyWideStringList
    stdlib_dir: nptr[u8]
    executable: nptr[u8]
    base_executable: nptr[u8]
    prefix: nptr[u8]
    base_prefix: nptr[u8]
    exec_prefix: nptr[u8]
    base_exec_prefix: nptr[u8]
    skip_source_first_line: i32
    run_command: nptr[u8]
    run_module: nptr[u8]
    run_filename: nptr[u8]
    sys_path_0: nptr[u8]
    _install_importlib: i32
    _init_main: i32
    _is_python_build: i32

struct PyStatus:
    typ: i32
    func: nptr[u8]
    err_msg: nptr[u8]
    exitcode: i32

struct PyMethodDef:
    ml_name: nptr[u8]
    ml_meth: nptr[u8]
    ml_flags: i32
    ml_doc: nptr[u8]

METH_VARARGS = 1
METH_NOARGS = 4
METH_O = 8
PYCONFIG_SIZE = 448
PYCONFIG_OFF_HOME = 296
PYSTATUS_SIZE = 32
PYMETHODDEF_SIZE = 32

extern def PyConfig_InitPythonConfig(cfg: ptr[PyConfig])
extern def PyConfig_SetBytesString(cfg: ptr[PyConfig], dest: ptr[nptr[u8]], s: ptr[u8]) -> PyStatus
extern def PyConfig_Clear(cfg: ptr[PyConfig])
extern def Py_InitializeFromConfig(cfg: ptr[PyConfig]) -> PyStatus
extern def Py_FinalizeEx() -> i32
extern def PyRun_SimpleString(s: ptr[u8]) -> i32
extern def PyModule_New(name: ptr[u8]) -> nptr[u8]
extern def PyModule_AddObject(mod: nptr[u8], name: ptr[u8], v: nptr[u8]) -> i32
extern def PyImport_GetModuleDict() -> nptr[u8]
extern def PyImport_ImportModule(name: ptr[u8]) -> nptr[u8]
extern def PyDict_SetItemString(d: nptr[u8], k: ptr[u8], v: nptr[u8]) -> i32
extern def PyCMethod_New(ml: ptr[PyMethodDef], self: nptr[u8], mod: nptr[u8], typ: nptr[u8]) -> nptr[u8]
extern def Py_IncRef(o: nptr[u8])
extern def Py_DecRef(o: nptr[u8])
extern def PyLong_FromLong(v: i64) -> nptr[u8]
extern def PyLong_AsLong(o: nptr[u8]) -> i64
extern def PyTuple_Size(o: nptr[u8]) -> i64
extern def PyTuple_GetItem(o: nptr[u8], i: i64) -> nptr[u8]
extern def PyObject_GetAttrString(o: nptr[u8], name: ptr[u8]) -> nptr[u8]
extern def PyErr_SetString(exc: nptr[u8], msg: ptr[u8])
extern def PyErr_Occurred() -> nptr[u8]
extern def PyErr_Print()
extern def malloc(n: u64) -> nptr[u8]
extern def strlen(s: ptr[u8]) -> u64
extern def write(fd: i32, buf: ptr[u8], n: u64) -> i64
extern def PyObject_Call(callable: nptr[u8], args: nptr[u8], kwargs: nptr[u8]) -> nptr[u8]
extern def PyObject_CallNoArgs(callable: nptr[u8]) -> nptr[u8]
extern def PyObject_CallOneArg(callable: nptr[u8], arg: nptr[u8]) -> nptr[u8]
extern def PyTuple_New(n: i64) -> nptr[u8]
extern def PyTuple_SetItem(t: nptr[u8], i: i64, v: nptr[u8]) -> i32
extern def PyImport_AddModule(name: ptr[u8]) -> nptr[u8]
extern def PyModule_GetDict(mod: nptr[u8]) -> nptr[u8]
extern def PyRun_String(src: ptr[u8], start: i32, globals: nptr[u8], locals: nptr[u8]) -> nptr[u8]

PY_FILE_INPUT = 257
PY_EVAL_INPUT = 258

g_py_ready: i32 = 0

def eputs(s: ptr[u8]):
    write(2, s, strlen(s))

def forge_py_layout_ok() -> i32:
    if sizeof(PyConfig) != PYCONFIG_SIZE:
        return 0
    if offsetof(PyConfig, home) != PYCONFIG_OFF_HOME:
        return 0
    if sizeof(PyStatus) != PYSTATUS_SIZE:
        return 0
    if sizeof(PyMethodDef) != PYMETHODDEF_SIZE:
        return 0
    return 1

@unsafe
def forge_py_init(prefix: nptr[u8]) -> i32:
    cfg: PyConfig
    st: PyStatus
    if prefix == 0:
        eputs("forge_py_init: missing prefix\n")
        return 1
    PyConfig_InitPythonConfig(addr(cfg))
    cfg.isolated = 1
    cfg.site_import = 0
    cfg.use_environment = 0
    cfg.install_signal_handlers = 0
    st = PyConfig_SetBytesString(addr(cfg), addr(cfg.home), prefix)
    if st.typ != 0:
        eputs("forge_py_init: failed to set home\n")
        PyConfig_Clear(addr(cfg))
        return 1
    st = PyConfig_SetBytesString(addr(cfg), addr(cfg.program_name), "forge-py-embed")
    if st.typ != 0:
        eputs("forge_py_init: failed to set program_name\n")
        PyConfig_Clear(addr(cfg))
        return 1
    st = Py_InitializeFromConfig(addr(cfg))
    PyConfig_Clear(addr(cfg))
    if st.typ != 0:
        eputs("forge_py_init: Py_InitializeFromConfig failed\n")
        return 1
    g_py_ready = 1
    return 0

def forge_py_run(src: nptr[u8]) -> i32:
    if g_py_ready == 0:
        eputs("forge_py_run: not initialized\n")
        return 1
    if src == 0:
        eputs("forge_py_run: missing source\n")
        return 1
    if PyRun_SimpleString(src) != 0:
        return 1
    return 0

def forge_py_finalize() -> i32:
    if g_py_ready == 0:
        return 0
    g_py_ready = 0
    if Py_FinalizeEx() < 0:
        eputs("forge_py_finalize: Py_FinalizeEx failed\n")
        return 1
    return 0

def forge_py_module_new(name: ptr[u8]) -> nptr[u8]:
    m = PyModule_New(name)
    if u64(m) == 0:
        eputs("forge_py_module_new: PyModule_New failed\n")
        PyErr_Print()
    return m

@unsafe
def forge_py_add_method(mod: nptr[u8], name: ptr[u8], fn: nptr[u8], flags: i32) -> i32:
    p = malloc(sizeof(PyMethodDef))
    if p == 0:
        eputs("forge_py_add_method: malloc failed\n")
        return 1
    d = ptr[PyMethodDef](u64(p))
    d.ml_name = name
    d.ml_meth = fn
    d.ml_flags = flags
    d.ml_doc = nptr[u8](u64(0))
    cfn = PyCMethod_New(d, nptr[u8](u64(0)), mod, nptr[u8](u64(0)))
    if u64(cfn) == 0:
        eputs("forge_py_add_method: PyCMethod_New failed\n")
        PyErr_Print()
        return 1
    if PyModule_AddObject(mod, name, cfn) != 0:
        eputs("forge_py_add_method: PyModule_AddObject failed\n")
        Py_DecRef(cfn)
        PyErr_Print()
        return 1
    return 0

def forge_py_module_install(mod: nptr[u8], name: ptr[u8]) -> i32:
    mods = PyImport_GetModuleDict()
    if u64(mods) == 0:
        eputs("forge_py_module_install: no sys.modules\n")
        return 1
    if PyDict_SetItemString(mods, name, mod) != 0:
        eputs("forge_py_module_install: PyDict_SetItemString failed\n")
        PyErr_Print()
        return 1
    Py_DecRef(mod)
    return 0

def py_arg_count(args: nptr[u8]) -> i64:
    if u64(args) == 0:
        return 0
    return PyTuple_Size(args)

def py_arg_i64(args: nptr[u8], i: i64) -> i64:
    return PyLong_AsLong(PyTuple_GetItem(args, i))

def py_from_i64(v: i64) -> nptr[u8]:
    return PyLong_FromLong(v)

@unsafe
def py_err(msg: ptr[u8]) -> nptr[u8]:
    b = PyImport_ImportModule("builtins")
    t: nptr[u8]
    if u64(b) == 0:
        PyErr_Print()
        return nptr[u8](u64(0))
    t = PyObject_GetAttrString(b, "TypeError")
    Py_DecRef(b)
    if u64(t) == 0:
        PyErr_Print()
        return nptr[u8](u64(0))
    PyErr_SetString(t, msg)
    Py_DecRef(t)
    return nptr[u8](u64(0))

def forge_py_main() -> nptr[u8]:
    m = PyImport_AddModule("__main__")
    if m == 0:
        eputs("forge_py_main: PyImport_AddModule failed\n")
        PyErr_Print()
    return m

def forge_py_eval(src: nptr[u8]) -> nptr[u8]:
    m: nptr[u8]
    d: nptr[u8]
    r: nptr[u8]
    if src == 0:
        eputs("forge_py_eval: missing source\n")
        return src
    m = forge_py_main()
    if m == 0:
        return m
    d = PyModule_GetDict(m)
    if d == 0:
        eputs("forge_py_eval: no __main__ dict\n")
        return d
    r = PyRun_String(src, PY_EVAL_INPUT, d, d)
    if r == 0:
        eputs("forge_py_eval: PyRun_String failed\n")
        PyErr_Print()
    return r

def forge_py_main_attr(name: ptr[u8]) -> nptr[u8]:
    m = forge_py_main()
    o: nptr[u8]
    if m == 0:
        return m
    o = PyObject_GetAttrString(m, name)
    if o == 0:
        eputs("forge_py_main_attr: lookup failed\n")
        PyErr_Print()
    return o

@unsafe
def forge_py_call_i64(fn: nptr[u8], nargs: i32, a: i64, b: i64, out: ptr[i64]) -> i32:
    r: nptr[u8]
    args: nptr[u8]
    aa: nptr[u8]
    bb: nptr[u8]
    if fn == 0:
        eputs("forge_py_call_i64: null callable\n")
        return 1
    if nargs == 0:
        r = PyObject_CallNoArgs(fn)
    elif nargs == 1:
        aa = PyLong_FromLong(a)
        if aa == 0:
            eputs("forge_py_call_i64: PyLong_FromLong failed\n")
            PyErr_Print()
            return 1
        r = PyObject_CallOneArg(fn, aa)
        Py_DecRef(aa)
    elif nargs == 2:
        args = PyTuple_New(2)
        if args == 0:
            eputs("forge_py_call_i64: PyTuple_New failed\n")
            PyErr_Print()
            return 1
        aa = PyLong_FromLong(a)
        bb = PyLong_FromLong(b)
        if aa == 0 or bb == 0:
            eputs("forge_py_call_i64: PyLong_FromLong failed\n")
            PyErr_Print()
            if aa != 0:
                Py_DecRef(aa)
            if bb != 0:
                Py_DecRef(bb)
            Py_DecRef(args)
            return 1
        if PyTuple_SetItem(args, 0, aa) != 0 or PyTuple_SetItem(args, 1, bb) != 0:
            eputs("forge_py_call_i64: PyTuple_SetItem failed\n")
            PyErr_Print()
            Py_DecRef(args)
            return 1
        r = PyObject_Call(fn, args, nptr[u8](u64(0)))
        Py_DecRef(args)
    else:
        eputs("forge_py_call_i64: nargs must be 0, 1, or 2\n")
        return 1
    if r == 0:
        eputs("forge_py_call_i64: call failed\n")
        PyErr_Print()
        return 1
    out[0] = PyLong_AsLong(r)
    Py_DecRef(r)
    if PyErr_Occurred() != 0:
        eputs("forge_py_call_i64: result is not an int\n")
        PyErr_Print()
        return 1
    return 0
