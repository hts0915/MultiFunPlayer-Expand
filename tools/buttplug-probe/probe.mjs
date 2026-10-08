import { ButtplugClient, ButtplugNodeWebsocketClientConnector } from "buttplug";
const url = process.argv[2] ?? "ws://127.0.0.1:12345";
const client = new ButtplugClient("Probe");
client.on("deviceadded", d => console.log("  EVENT deviceadded:", d.name, "#" + d.index));
const killer = setTimeout(() => { console.log("  !! 超时（6 秒）"); process.exit(1); }, 6000);
try {
  await client.connect(new ButtplugNodeWebsocketClientConnector(url));
  console.log("  ✓ connect() 成功");
  await client.startScanning();
  console.log("  ✓ startScanning() 成功；设备:", client.devices.map(d => `${d.name}#${d.index}`).join(",") || "(空)");
  for (const d of client.devices) console.log("    -", d.name, "支持:", d.messageAttributes ? [...d.messageAttributes.keys()].join("/") : "(无)");
  await client.disconnect();
  console.log("  ✓ 全部完成");
} catch (e) { console.log("  ✗ 失败:", e?.message ?? e); }
clearTimeout(killer);
process.exit(0);
