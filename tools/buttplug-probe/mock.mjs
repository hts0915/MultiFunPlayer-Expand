import { WebSocketServer } from "ws";
const serverInfo = { ServerInfo: { Id: 1, MajorVersion: 3, MinorVersion: 0, MessageVersion: 3, MaxPingTime: 0, ServerName: "MultiFunPlayer" } };
const devices = [{ DeviceName: "OSR6", DeviceIndex: 0, DeviceMessages: {
  LinearCmd: [ { ActuatorType: "Position", FeatureDescriptor: "Main Stroke (L0)", StepCount: 100 } ],
  RotateCmd: [ { ActuatorType: "Rotate", FeatureDescriptor: "Twist (R0)", StepCount: 100 } ],
  ScalarCmd: [],
  StopDeviceCmd: {} } }];
const wss = new WebSocketServer({ port: 12399 });
wss.on("connection", ws => ws.on("message", data => {
  for (const m of JSON.parse(data.toString())) {
    const type = Object.keys(m)[0];
    if (type === "RequestServerInfo") ws.send(JSON.stringify([serverInfo]));
    else if (type === "RequestDeviceList") ws.send(JSON.stringify([{ DeviceList: { Id: m.Id, Devices: devices } }]));
    else if (type === "StartScanning") { ws.send(JSON.stringify([{ Ok: { Id: m.Id } }])); ws.send(JSON.stringify([{ ScanningFinished: { Id: 0 } }])); }
    else ws.send(JSON.stringify([{ Ok: { Id: m.Id } }]));
  }
}));
setTimeout(() => process.exit(0), 25000);
