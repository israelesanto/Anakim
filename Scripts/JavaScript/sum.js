function handle(g) {
    const a = Number(g.Args?.a ?? 0);
    const b = Number(g.Args?.b ?? 0);
    if (typeof logInfo === "function") logInfo(`Somando ${a} + ${b}`);
    return { ok: true, sum: a + b, at: (g.NowUtc || new Date()).toString() };
}
