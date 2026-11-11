using System;
using TaoKeeper.Core.Native;
using TaoKeeper.Core.Modules;
Console.WriteLine($"IsAvailable={TaoKeeperScanNative.IsAvailable} err={TaoKeeperScanNative.LastLoadError ?? "none"}");
var s = MftStreamSession.TryCreate(1024);
Console.WriteLine($"session={(s is null ? "null" : "ok")}");
