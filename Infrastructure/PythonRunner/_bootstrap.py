# _bootstrap.py
# Executa um script Python simples que define `def handle(g): ...`.
# Uso: python _bootstrap.py <script_path>
import sys, json, importlib.util, datetime, types, traceback

def _default(o):
    if isinstance(o, (datetime.datetime, datetime.date, datetime.time)):
        return o.isoformat()
    return str(o)

def _load_module(path):
    spec = importlib.util.spec_from_file_location("anakim_script", path)
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)  # type: ignore
    return mod

def main():
    if len(sys.argv) < 2:
        print(json.dumps({"ok": False, "error": "missing_script_path"}))
        return

    script_path = sys.argv[1]
    try:
        mod = _load_module(script_path)
        if not hasattr(mod, "handle") or not isinstance(mod.handle, types.FunctionType):
            print(json.dumps({"ok": False, "error": "missing_handle"}))
            return

        g = json.load(sys.stdin)
        res = mod.handle(g)
        print(json.dumps(res, default=_default, ensure_ascii=False))
    except Exception as e:
        print(json.dumps({
            "ok": False,
            "error": "python_exception",
            "detail": f"{type(e).__name__}: {e}",
            "trace": traceback.format_exc()
        }, ensure_ascii=False))

if __name__ == "__main__":
    main()
