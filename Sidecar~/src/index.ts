// src/index.ts
import { createServer } from "./server.js";

function parseArgs(args: string[]): { port: number; mcpPort: number } {
  let port = 0;
  let mcpPort = 0;

  for (let i = 0; i < args.length; i++) {
    if (args[i] === "--port" && args[i + 1]) {
      port = parseInt(args[i + 1], 10);
      i++;
    } else if (args[i] === "--mcp-port" && args[i + 1]) {
      mcpPort = parseInt(args[i + 1], 10);
      i++;
    }
  }

  if (!mcpPort) {
    console.error("Error: --mcp-port is required");
    process.exit(1);
  }

  return { port, mcpPort };
}

const { port, mcpPort } = parseArgs(process.argv.slice(2));

// The auth token is supplied by Unity via env var. Without it the sidecar would
// expose its HTTP API to any local process and the Unity MCP server would reject
// every callback — refuse to start so a misconfigured deployment fails loudly.
const authToken = process.env.UNICLAUDE_AUTH_TOKEN ?? "";
if (!authToken || authToken.length < 16) {
  console.error(
    "Error: UNICLAUDE_AUTH_TOKEN env var is missing or too short. " +
      "This binary is intended to be spawned by the Unity Editor, not run directly."
  );
  process.exit(1);
}

createServer({ port, mcpPort, authToken });
