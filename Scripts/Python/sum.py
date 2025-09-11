import datetime

def handle(g):
    args = g.get("Args", {}) or {}
    a = float(args.get("a", 0))
    b = float(args.get("b", 0))
    return {
        "ok": True,
        "sum": a + b,
        "at": datetime.datetime.utcnow().isoformat() + "Z"
    }
