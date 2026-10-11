// 把某个 Buttplug 服务端发回的原始消息逐条打印出来（用于与 Intiface Central 对齐）
// 用法： node dump.mjs ws://127.0.0.1:12345
// 说明：故意用 ClientName = "Lustbound"，让服务端走与游戏相同的兼容分支，便于逐字比对。
import { WebSocket } from "ws";

const url = process.argv[2] ?? "ws://127.0.0.1:12345";
const clientName = process.argv[3] ?? "Lustbound";
const messageVersion = Number(process.argv[4] ?? 3);   // 3 = 新版客户端；1/2 = 老库（DeviceMessages 形态不同）

const ws = new WebSocket(url);
let id = 0;

ws.on("open", () => {
    console.log("=== 已连接:", url, " 伪装客户端名:", clientName, " 协议版本:", messageVersion, "===");
    ws.send(JSON.stringify([{ RequestServerInfo: { Id: ++id, ClientName: clientName, MessageVersion: messageVersion } }]));
});

ws.on("message", (data) => {
    let msgs;
    try { msgs = JSON.parse(data.toString()); } catch (e) { console.log("<<< (无法解析)", data.toString()); return; }

    for (const m of msgs) {
        console.log("<<< " + JSON.stringify(m));
        const type = Object.keys(m)[0];
        if (type === "ServerInfo") {
            const deviceList = { RequestDeviceList: { Id: ++id } };
            console.log(">>> " + JSON.stringify(deviceList));
            ws.send(JSON.stringify([deviceList]));
        } else if (type === "DeviceList") {
            const scan = { StartScanning: { Id: ++id } };
            console.log(">>> " + JSON.stringify(scan));
            ws.send(JSON.stringify([scan]));
        } else if (type === "ScanningFinished") {
            console.log("--- 扫描结束；再取一次设备列表以确认 ---");
            const again = { RequestDeviceList: { Id: ++id } };
            ws.send(JSON.stringify([again]));
        }
    }
});

ws.on("error", (e) => console.log("!! 连接错误:", e.message));
ws.on("close", (c) => console.log("=== 连接关闭 code=" + c + " ==="));

setTimeout(() => { try { ws.close(); } catch {} process.exit(0); }, 8000);
