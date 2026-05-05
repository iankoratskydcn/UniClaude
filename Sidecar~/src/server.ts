// src/server.ts
import express, { NextFunction, Request, Response } from "express";
import { timingSafeEqual } from "node:crypto";
import { AgentRunner } from "./agent.js";
import type {
  ChatRequest,
  ApproveRequest,
  DenyRequest,
  HealthResponse,
  SSEEvent,
} from "./types.js";

const VERSION = "0.1.0";

export interface ServerOptions {
  port: number;
  mcpPort: number;
  /** Shared secret. Required on every endpoint via Authorization: Bearer or ?token=. */
  authToken: string;
}

function constantTimeEqual(a: string, b: string): boolean {
  // Compare equal-length buffers in constant time. timingSafeEqual throws when
  // lengths differ, so we always compare same-length buffers (and let the
  // length-mismatch case fall through to a normal mismatch result).
  const ab = Buffer.from(a, "utf8");
  const bb = Buffer.from(b, "utf8");
  if (ab.length !== bb.length) {
    // Still do a constant-time compare against ourselves to avoid a fast-path
    // length oracle. The result is always false because a !== b.
    timingSafeEqual(ab, ab);
    return false;
  }
  return timingSafeEqual(ab, bb);
}

const LOOPBACK_HOSTS = new Set(["127.0.0.1", "localhost", "[::1]", "::1"]);
function isLoopbackHost(hostHeader: string | undefined): boolean {
  if (!hostHeader) return true; // Some HTTP/1.0 clients omit Host.
  const trimmed = hostHeader.trim();
  if (trimmed.startsWith("[")) {
    const end = trimmed.indexOf("]");
    if (end < 0) return false;
    const ip = trimmed.slice(1, end);
    return LOOPBACK_HOSTS.has(ip) || LOOPBACK_HOSTS.has(`[${ip}]`);
  }
  const colon = trimmed.lastIndexOf(":");
  const host = colon > 0 ? trimmed.slice(0, colon) : trimmed;
  return LOOPBACK_HOSTS.has(host.toLowerCase());
}

export function createServer(options: ServerOptions) {
  if (!options.authToken || options.authToken.length < 16) {
    throw new Error("createServer: authToken is required (>= 16 chars)");
  }

  const app = express();
  app.use(express.json({ limit: "10mb" }));

  // Auth + DNS-rebind guard. Mounted before any route so all endpoints inherit it.
  app.use((req: Request, res: Response, next: NextFunction) => {
    if (!isLoopbackHost(req.headers.host)) {
      res.status(403).end();
      return;
    }
    const header = req.headers["authorization"];
    let presented: string | null = null;
    if (typeof header === "string" && header.toLowerCase().startsWith("bearer ")) {
      presented = header.slice("bearer ".length).trim();
    } else if (typeof req.query.token === "string") {
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
  let sseClient: Response | null = null;
  let eventSeq = 0;
  let queryEventBuffer: Array<{ id: number; data: string }> = [];

  function emitSSE(event: SSEEvent): void {
    const id = ++eventSeq;
    const data = JSON.stringify(event);
    queryEventBuffer.push({ id, data });

    if (sseClient) {
      sseClient.write(`id: ${id}\ndata: ${data}\n\n`);
    }
    // If no client connected, events are still buffered for replay
  }

  const agent = new AgentRunner({
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

  app.get("/health", (_req: Request, res: Response) => {
    lastHealthPing = Date.now();
    const response: HealthResponse = {
      status: "ok",
      version: VERSION,
      query_active: agent.isQueryActive,
      trusted_tools: agent.trustedTools,
    };
    res.json(response);
  });

  app.get("/stream", (req: Request, res: Response) => {
    // Close previous SSE client if any
    if (sseClient) {
      sseClient.end();
    }

    res.writeHead(200, {
      "Content-Type": "text/event-stream",
      "Cache-Control": "no-cache",
      Connection: "keep-alive",
    });

    // Replay missed events if Last-Event-ID is provided
    const lastId = parseInt(req.headers["last-event-id"] as string, 10);
    if (!isNaN(lastId)) {
      for (const entry of queryEventBuffer) {
        if (entry.id > lastId) {
          res.write(`id: ${entry.id}\ndata: ${entry.data}\n\n`);
        }
      }
    }

    sseClient = res;

    // SSE keep-alive
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

  app.post("/chat", async (req: Request, res: Response) => {
    if (agent.isQueryActive) {
      res.status(409).json({ error: "A query is already active. Cancel first." });
      return;
    }

    const request = req.body as ChatRequest;
    const hasAttachments = request.attachments && request.attachments.length > 0;
    if (!request.message && !hasAttachments) {
      res.status(400).json({ error: "message or attachments required" });
      return;
    }

    // Clear previous query's event buffer
    queryEventBuffer = [];

    // Respond immediately — events flow via SSE
    res.json({ ok: true });

    // Start query in background (events emitted via SSE)
    agent.startQuery(request).catch((err) => {
      emitSSE({ type: "error", message: String(err) });
    });
  });

  app.post("/approve", (req: Request, res: Response) => {
    const { id, type, answer } = req.body as ApproveRequest & { answer?: string };
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

  app.post("/deny", (req: Request, res: Response) => {
    const { id } = req.body as DenyRequest;
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

  app.post("/cancel", (_req: Request, res: Response) => {
    agent.cancelQuery();
    res.json({ ok: true });
  });

  app.post("/undo", async (_req: Request, res: Response) => {
    const result = await agent.undo();
    res.json(result);
  });

  // ── Start ──

  const server = app.listen(options.port, "127.0.0.1", () => {
    const addr = server.address();
    const actualPort =
      typeof addr === "object" && addr ? addr.port : options.port;
    console.log(
      JSON.stringify({ status: "started", port: actualPort, version: VERSION })
    );
  });

  // Cleanup on exit
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
