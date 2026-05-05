"use strict";
var __importDefault = (this && this.__importDefault) || function (mod) {
    return (mod && mod.__esModule) ? mod : { "default": mod };
};
Object.defineProperty(exports, "__esModule", { value: true });
exports.createServer = createServer;
// src/server.ts
const express_1 = __importDefault(require("express"));
const node_crypto_1 = require("node:crypto");
const agent_js_1 = require("./agent.js");
const VERSION = "0.1.0";
function constantTimeEqual(a, b) {
    const ab = Buffer.from(a, "utf8");
    const bb = Buffer.from(b, "utf8");
    if (ab.length !== bb.length) {
        (0, node_crypto_1.timingSafeEqual)(ab, ab);
        return false;
    }
    return (0, node_crypto_1.timingSafeEqual)(ab, bb);
}
const LOOPBACK_HOSTS = new Set(["127.0.0.1", "localhost", "[::1]", "::1"]);
function isLoopbackHost(hostHeader) {
    if (!hostHeader)
        return true;
    const trimmed = hostHeader.trim();
    if (trimmed.startsWith("[")) {
        const end = trimmed.indexOf("]");
        if (end < 0)
            return false;
        const ip = trimmed.slice(1, end);
        return LOOPBACK_HOSTS.has(ip) || LOOPBACK_HOSTS.has(`[${ip}]`);
    }
    const colon = trimmed.lastIndexOf(":");
    const host = colon > 0 ? trimmed.slice(0, colon) : trimmed;
    return LOOPBACK_HOSTS.has(host.toLowerCase());
}
function createServer(options) {
    if (!options.authToken || options.authToken.length < 16) {
        throw new Error("createServer: authToken is required (>= 16 chars)");
    }
    const app = (0, express_1.default)();
    app.use(express_1.default.json({ limit: "10mb" }));
    // Auth + DNS-rebind guard. Mounted before any route so all endpoints inherit it.
    app.use((req, res, next) => {
        if (!isLoopbackHost(req.headers.host)) {
            res.status(403).end();
            return;
        }
        const header = req.headers["authorization"];
        let presented = null;
        if (typeof header === "string" && header.toLowerCase().startsWith("bearer ")) {
            presented = header.slice("bearer ".length).trim();
        }
        else if (typeof req.query.token === "string") {
            presented = req.query.token;
        }
        if (!presented || !constantTimeEqual(options.authToken, presented)) {
            res
                .status(401)
                .setHeader("WWW-Authenticate", 'Bearer realm="uniclaude-sidecar"')
                .end();
            return;
        }
        next();
    });
    // SSE client management with event buffering for reconnect
    let sseClient = null;
    let eventSeq = 0;
    let queryEventBuffer = [];
    function emitSSE(event) {
        const id = ++eventSeq;
        const data = JSON.stringify(event);
        queryEventBuffer.push({ id, data });
        if (sseClient) {
            sseClient.write(`id: ${id}\ndata: ${data}\n\n`);
        }
        // If no client connected, events are still buffered for replay
    }
    const agent = new agent_js_1.AgentRunner({
        mcpPort: options.mcpPort,
        authToken: options.authToken,
        onEvent: emitSSE,
    });
    // Heartbeat tracking
    let lastHealthPing = Date.now();
    const HEARTBEAT_TIMEOUT_MS = 60_000;
    const heartbeatInterval = setInterval(() => {
        if (Date.now() - lastHealthPing > HEARTBEAT_TIMEOUT_MS) {
            console.log("[sidecar] No health ping in 60s — shutting down");
            process.exit(0);
        }
    }, 10_000);
    // ── Routes ──
    app.get("/health", (_req, res) => {
        lastHealthPing = Date.now();
        const response = {
            status: "ok",
            version: VERSION,
            query_active: agent.isQueryActive,
            trusted_tools: agent.trustedTools,
        };
        res.json(response);
    });
    app.get("/stream", (req, res) => {
        if (sseClient) {
            sseClient.end();
        }
        res.writeHead(200, {
            "Content-Type": "text/event-stream",
            "Cache-Control": "no-cache",
            Connection: "keep-alive",
        });
        const lastId = parseInt(req.headers["last-event-id"], 10);
        if (!isNaN(lastId)) {
            for (const entry of queryEventBuffer) {
                if (entry.id > lastId) {
                    res.write(`id: ${entry.id}\ndata: ${entry.data}\n\n`);
                }
            }
        }
        sseClient = res;
        const keepAlive = setInterval(() => {
            res.write(": keepalive\n\n");
        }, 15_000);
        res.on("close", () => {
            clearInterval(keepAlive);
            if (sseClient === res) {
                sseClient = null;
            }
        });
    });
    app.post("/chat", async (req, res) => {
        if (agent.isQueryActive) {
            res.status(409).json({ error: "A query is already active. Cancel first." });
            return;
        }
        const request = req.body;
        const hasAttachments = request.attachments && request.attachments.length > 0;
        if (!request.message && !hasAttachments) {
            res.status(400).json({ error: "message or attachments required" });
            return;
        }
        queryEventBuffer = [];
        res.json({ ok: true });
        agent.startQuery(request).catch((err) => {
            emitSSE({ type: "error", message: String(err) });
        });
    });
    app.post("/approve", (req, res) => {
        const { id, type, answer } = req.body;
        if (!id || !type) {
            res.status(400).json({ error: "id and type are required" });
            return;
        }
        const resolved = agent.resolvePermission(id, { type, answer });
        if (!resolved) {
            res.status(404).json({ error: "No pending request with this id" });
            return;
        }
        res.json({ ok: true });
    });
    app.post("/deny", (req, res) => {
        const { id } = req.body;
        if (!id) {
            res.status(400).json({ error: "id is required" });
            return;
        }
        const resolved = agent.resolvePermission(id, { type: "deny" });
        if (!resolved) {
            res.status(404).json({ error: "No pending request with this id" });
            return;
        }
        res.json({ ok: true });
    });
    app.post("/cancel", (_req, res) => {
        agent.cancelQuery();
        res.json({ ok: true });
    });
    app.post("/undo", async (_req, res) => {
        const result = await agent.undo();
        res.json(result);
    });
    // ── Start ──
    const server = app.listen(options.port, "127.0.0.1", () => {
        const addr = server.address();
        const actualPort = typeof addr === "object" && addr ? addr.port : options.port;
        console.log(JSON.stringify({ status: "started", port: actualPort, version: VERSION }));
    });
    process.on("SIGTERM", () => {
        clearInterval(heartbeatInterval);
        server.close();
        process.exit(0);
    });
    process.on("SIGINT", () => {
        clearInterval(heartbeatInterval);
        server.close();
        process.exit(0);
    });
    return server;
}
//# sourceMappingURL=server.js.map
