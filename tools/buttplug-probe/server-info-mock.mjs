import { WebSocketServer } from "ws";
const compat = process.argv[2] === "compat";
const si = compat
  ? { Id: 1, ServerName: "MultiFunPlayer", MessageVersion: 3 }
  : { Id: 1, ServerName: "MultiFunPlayer", MajorVersion: 3, MinorVersion: 0, MessageVersion: 3, MaxPingTime: 0 };
const wss = new WebSocketServer({ port: 12399 });
wss.on("connection", ws => ws.on("message", () => ws.send(JSON.stringify([{ ServerInfo: si }]))));
setTimeout(() => process.exit(0), 12000);
