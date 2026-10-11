import { WebSocket } from "ws";
// 模拟两种客户端的报错规则：老库不认识 MajorVersion / MinorVersion / MaxPingTime
const strictOld = process.argv[2] === "old";
const ws = new WebSocket("ws://127.0.0.1:12399");
ws.on("open", () => ws.send(JSON.stringify([{ RequestServerInfo: { Id: 1, ClientName: "Lustbound", MessageVersion: 3 } }])));
ws.on("message", d => {
  for (const m of JSON.parse(d.toString())) {
    const t = Object.keys(m)[0];
    if (t !== "ServerInfo") continue;
    const known = ["Id", "ServerName", "MessageVersion"];
    const unknown = Object.keys(m.ServerInfo).filter(k => !known.includes(k));
    console.log("  收到 ServerInfo:", JSON.stringify(m.ServerInfo));
    if (strictOld && unknown.length) { console.log("  ✗ 老库报错: Could not find member '" + unknown[0] + "'"); }
    else console.log("  ✓ 老库接受这份 ServerInfo");
    process.exit(0);
  }
});
setTimeout(() => { console.log("  !! 超时"); process.exit(1); }, 4000);
