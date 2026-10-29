using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using Il2CppInterop.Runtime;
using MelonLoader;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

namespace SpellBrigadeMods.CrabLand
{
    /// <summary>
    /// Host → clients: "CrabLand.State" [protocol, active, bossScale*10, version...].
    /// Client → host: "CrabLand.Hello" [protocol, version...], so the host can log who runs the mod.
    /// A client without the mod has no handler; NGO drops the message and the host logs the missing reply.
    /// </summary>
    internal static class Net
    {
        private const string StateMsg = "CrabLand.State";
        private const string HelloMsg = "CrabLand.Hello";
        private const string RewardMsg = "CrabLand.Reward";
        private const byte Protocol = 1;

        private static IntPtr _registeredOn;   // CustomMessagingManager the handlers live on
        private static IntPtr _callbackOn;     // NetworkManager the connect callback lives on
        private static readonly HashSet<ulong> Replied = new HashSet<ulong>();

        private static MelonLogger.Instance Log => CrabLandMod.Log;

        public static void EnsureRegistered()
        {
            var nm = NetworkManager.Singleton;
            var cmm = nm?.CustomMessagingManager;
            if (cmm == null) { Log.Warning("net: no CustomMessagingManager yet"); return; }

            if (_registeredOn != cmm.Pointer)
            {
                cmm.RegisterNamedMessageHandler(StateMsg, new Action<ulong, FastBufferReader>(OnState));
                cmm.RegisterNamedMessageHandler(HelloMsg, new Action<ulong, FastBufferReader>(OnHello));
                cmm.RegisterNamedMessageHandler(RewardMsg, new Action<ulong, FastBufferReader>(OnReward));
                _registeredOn = cmm.Pointer;
                Replied.Clear();
                Log.Msg($"net: handlers registered server={nm.IsServer} client={nm.IsClient} localId={nm.LocalClientId}");
            }

            if (nm.IsServer && _callbackOn != nm.Pointer)
            {
                nm.add_OnClientConnectedCallback(DelegateSupport.ConvertDelegate<Il2CppSystem.Action<ulong>>(
                    new Action<ulong>(OnClientConnected)));
                _callbackOn = nm.Pointer;
            }

            if (nm.IsServer) BroadcastState();
        }

        private static void OnClientConnected(ulong clientId)
        {
            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsServer || clientId == nm.LocalClientId) return;
            Log.Msg($"net: client {clientId} connected; sending state");
            Send(clientId);
            MelonCoroutines.Start(ExpectHello(clientId));
        }

        private static IEnumerator ExpectHello(ulong clientId)
        {
            yield return new WaitForSeconds(10f);
            if (!Replied.Contains(clientId))
                Log.Warning($"net: client {clientId} sent no {HelloMsg} in 10s (mod missing or protocol mismatch); it will see native enemies spawn as crabs but no reskin or boss scale");
        }

        public static void BroadcastState()
        {
            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsListening || !nm.IsServer) return;
            Send(null);
        }

        /// <summary>Host: every mod client opens its own infusion pick (the host's own call is direct).</summary>
        public static void BroadcastReward()
        {
            var nm = NetworkManager.Singleton;
            var cmm = nm?.CustomMessagingManager;
            if (cmm == null || !nm.IsListening || !nm.IsServer) return;
            SendBytes(cmm, RewardMsg, null, new List<byte> { Protocol });
            Log.Msg("net: sent reward to all");
        }

        private static void OnReward(ulong sender, FastBufferReader reader)
        {
            ReadBytes(reader);
            Log.Msg($"net: reward from {sender}");
            Run.GrantLocalInfusion();
        }

        private static void Send(ulong? clientId)
        {
            var cmm = NetworkManager.Singleton?.CustomMessagingManager;
            if (cmm == null) return;
            var payload = new List<byte> { Protocol, (byte)(CrabLandMod.Active ? 1 : 0),
                (byte)Mathf.Clamp(Mathf.RoundToInt(CrabLandMod.BossScale.Value * 10f), 1, 255) };
            payload.AddRange(Encoding.ASCII.GetBytes(CrabLandMod.Version));
            SendBytes(cmm, StateMsg, clientId, payload);
            Log.Msg($"net: sent state to {(clientId.HasValue ? clientId.Value.ToString() : "all")} active={CrabLandMod.Active}");
        }

        private static void SendBytes(CustomMessagingManager cmm, string name, ulong? clientId, List<byte> payload)
        {
            var w = new FastBufferWriter(payload.Count + 1, Allocator.Temp, -1);
            try
            {
                w.WriteByteSafe((byte)payload.Count);
                foreach (var b in payload) w.WriteByteSafe(b);
                // ToAll on the host may include the host itself; OnState ignores it there.
                if (clientId.HasValue) cmm.SendNamedMessage(name, clientId.Value, w, NetworkDelivery.ReliableSequenced);
                else cmm.SendNamedMessageToAll(name, w, NetworkDelivery.ReliableSequenced);
            }
            catch (Exception e) { Log.Error($"net: send {name} to {clientId} failed: {e.Message}"); }
            finally { w.Dispose(); }
        }

        private static byte[] ReadBytes(FastBufferReader r)
        {
            r.ReadByteSafe(out byte n);
            var bytes = new byte[n];
            for (var i = 0; i < n; i++) { r.ReadByteSafe(out byte b); bytes[i] = b; }
            return bytes;
        }

        private static void OnState(ulong sender, FastBufferReader reader)
        {
            try
            {
                var nm = NetworkManager.Singleton;
                if (nm != null && nm.IsServer) return;
                var p = ReadBytes(reader);
                if (p.Length < 3 || p[0] != Protocol) { Log.Warning($"net: state protocol={(p.Length > 0 ? p[0] : -1)} len={p.Length}, ignored"); return; }
                var hostVersion = Encoding.ASCII.GetString(p, 3, p.Length - 3);
                Log.Msg($"net: state from {sender} active={p[1]} bossScaleX10={p[2]} hostVersion={hostVersion} ours={CrabLandMod.Version}");
                CrabLandMod.HostBossScale = p[2] / 10f;
                CrabLandMod.Active = p[1] == 1;
                if (CrabLandMod.Active && CrabLandMod.InBaseWorldScene()) Reskin.Apply();

                var hello = new List<byte> { Protocol };
                hello.AddRange(Encoding.ASCII.GetBytes(CrabLandMod.Version));
                SendBytes(nm.CustomMessagingManager, HelloMsg, NetworkManager.ServerClientId, hello);
            }
            catch (Exception e) { Log.Error($"net: bad state message: {e.Message}"); }
        }

        private static void OnHello(ulong sender, FastBufferReader reader)
        {
            try
            {
                var p = ReadBytes(reader);
                var version = p.Length > 1 ? Encoding.ASCII.GetString(p, 1, p.Length - 1) : "";
                Replied.Add(sender);
                Log.Msg($"net: client {sender} runs CrabLand protocol={(p.Length > 0 ? p[0] : -1)} version={version}");
            }
            catch (Exception e) { Log.Error($"net: bad hello message: {e.Message}"); }
        }
    }
}
