using System;
using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;
using Newtonsoft.Json;

namespace PawnPilot
{
    public sealed class ProbeBehaviour : MonoBehaviour
    {
        private ModLog log;
        private SessionConfig config;
        private readonly string session = Guid.NewGuid().ToString("N");
        private readonly ActionGate gate = new ActionGate();
        private readonly WorldVersion worldVersion = new WorldVersion();
        private readonly WorldIdentityTracker worldIdentity = new WorldIdentityTracker();
        private readonly ReadyFrameTracker readyFrames = new ReadyFrameTracker();
        private UnityWebRequest current;
        private bool stopped, subscribed;
        private int activeSceneHandle = -1;
        private string brokerEpoch;
        private ReadinessSignals latestSignals = new ReadinessSignals();
        private Readiness latestReadiness = GameReadiness.Evaluate(null, null);
        private ActionResult pendingResult;
        private bool connectionReported;
        private bool failureReported;
        // 使用游戏自带 Json.NET；禁止类型名驱动实例化，限制反序列化深度。
        private static readonly JsonSerializerSettings JsonSettings = new JsonSerializerSettings
        {
            TypeNameHandling = TypeNameHandling.None,
            MetadataPropertyHandling = MetadataPropertyHandling.Ignore,
            MaxDepth = 16
        };
        private static string ToJson(object value) => JsonConvert.SerializeObject(value, JsonSettings);

        public void Initialize(ModLog logger)
        {
            log = logger;
            ModHook.OnGameLoadedLate += OnGameLoaded;
            SceneManager.activeSceneChanged += SceneChanged;
            activeSceneHandle = SceneManager.GetActiveScene().handle;
            subscribed = true;
            StartCoroutine(Bootstrap());
        }

        private void Update() => RefreshReadinessFrame();

        private IEnumerator Bootstrap()
        {
            // 正式版 LoadModDll 在 OnEnable 返回后才登记 LoadedMod；需等下一帧。
            yield return null;
            try { ConfigureBridge(); }
            catch (Exception ex)
            {
                log.Error("PAWNPILOT_INITIALIZATION_FAILED bootstrap " + ex.GetType().Name);
                Shutdown();
            }
        }

        private void ConfigureBridge()
        {
            // 使用游戏提供的受限 Mod 文件读取，不访问存档或任意本机路径。
            string json = ModHelper.ReadModFileText("pawnpilot_m0", "session.json");
            if (string.IsNullOrEmpty(json))
            {
                log.Log("PAWNPILOT_LOAD_ONLY: no session.json; bridge disabled");
                return;
            }
            config = JsonConvert.DeserializeObject<SessionConfig>(json, JsonSettings);
            if (config == null || config.port != 18765 || config.token == null || config.token.Length < 32)
                throw new InvalidOperationException("Invalid session configuration");
            StartCoroutine(Poll());
            log.Log("PAWNPILOT_BRIDGE_READY");
        }

        private void OnGameLoaded()
        {
            // LoadGame fires this synchronously while EmporiumEntry.Start is still running.
            // Invalidate here, but never capture/declare ready from inside the load hook.
            worldIdentity.OnGameLoaded(CurrentWorldIdentity(), worldVersion);
            ResetReadinessForWorldChange();
        }
        private void SceneChanged(Scene before, Scene after)
        {
            activeSceneHandle = after.handle;
            worldIdentity.OnSceneChanged(worldVersion);
            ResetReadinessForWorldChange();
            latestSignals = new ReadinessSignals();
            latestReadiness = GameReadiness.Evaluate(latestSignals, null);
        }

        private ReadinessSignals ReadSignals()
        {
            var scene = SceneManager.GetActiveScene();
            bool gameplay = scene.handle == activeSceneHandle && scene.name == "InventoryScene";
            bool hasStore = PlayerStore.HasInstance;
            PlayerStore store = hasStore ? PlayerStore.Instance : null;
            bool initialized = store != null && store.saveSlotId >= 0 && !string.IsNullOrEmpty(store.runID) &&
                store.saveVersion == PlayerStore.VERSION && store.networkUpgrade != null && store.storeService != null;
            var entry = EmporiumEntry.Instance;
            bool inventoryBuilt = entry != null && entry.invWindow != null && entry.invElement != null &&
                entry.frontInvWindow != null && entry.frontInvinvElement != null;
            var ui = StoreUIManager.Instance;
            bool uiInitialized = ui != null && ui.isActiveAndEnabled && ui.gameObject.activeInHierarchy &&
                ui.escapeUIManager != null && ui.escapeUIManager.isActiveAndEnabled && ui.escapeUIManager.menuPanel != null && inventoryBuilt;
            return new ReadinessSignals
            {
                gameplay_scene = gameplay,
                store_present = hasStore,
                store_initialized = initialized,
                ui_initialized = uiInitialized,
                stable_ready_frames = readyFrames.Count
            };
        }

        private string CurrentWorldIdentity()
        {
            if (!PlayerStore.HasInstance) return null;
            var store = PlayerStore.Instance;
            if (store == null || string.IsNullOrEmpty(store.runID) || store.saveSlotId < 0) return null;
            return store.saveSlotId + ":" + store.runID;
        }

        private void ResetReadinessForWorldChange()
        {
            readyFrames.Reset(Time.frameCount);
        }

        private void EnsureWorldIdentityCurrent()
        {
            if (worldIdentity.Ensure(CurrentWorldIdentity(), worldVersion))
                ResetReadinessForWorldChange();
        }

        private void RefreshReadinessFrame()
        {
            var scene = SceneManager.GetActiveScene();
            if (activeSceneHandle != scene.handle)
            {
                activeSceneHandle = scene.handle;
                worldIdentity.OnSceneChanged(worldVersion);
                ResetReadinessForWorldChange();
            }
            EnsureWorldIdentityCurrent();

            var signals = ReadSignals();
            readyFrames.Observe(IsReadinessCandidate(signals), Time.frameCount, advance:true);
            signals.stable_ready_frames = readyFrames.Count;
            latestSignals = signals;
            string phase = signals.store_initialized ? PlayerStore.Instance.storeState.ToString() : null;
            latestReadiness = GameReadiness.Evaluate(signals, phase);
        }

        private Snapshot Capture()
        {
            // Poll/command callbacks may occur between Update calls; refresh the signals but
            // only Update advances the two-frame stability barrier.
            EnsureWorldIdentityCurrent();
            latestSignals = ReadSignals();
            // A fresh negative observation invalidates readiness immediately. A positive
            // capture never advances the frame count, even if Update has not run yet.
            readyFrames.Observe(IsReadinessCandidate(latestSignals), Time.frameCount, advance:false);
            latestSignals.stable_ready_frames = readyFrames.Count;
            string currentPhase = latestSignals.store_initialized ? PlayerStore.Instance.storeState.ToString() : null;
            latestReadiness = GameReadiness.Evaluate(latestSignals, currentPhase);
            var scene = SceneManager.GetActiveScene();
            var ui = StoreUIManager.Instance;
            bool loaded = latestReadiness.loaded;
            bool ready = latestReadiness.ready;
            var menu = ready ? ui.escapeUIManager : null;
            bool open = menu != null && menu.menuPanel != null && menu.menuPanel.activeInHierarchy;
            var state = new Snapshot
            {
                schema_version = Protocol.SchemaVersion,
                world_epoch = worldVersion.Epoch,
                revision = worldVersion.Revision,
                scene = scene.name, loaded = loaded, ready = ready,
                cash = loaded ? PlayerStore.Instance.GetCash() : 0,
                phase = latestReadiness.phase,
                not_ready_reason = latestReadiness.reason,
                menu_open = open,
                can_close_menu = ready && open && menu.isActiveAndEnabled
            };
            state.revision = 0;
            state.version = null;
            string signature = ToJson(state);
            state.version = worldVersion.Observe(signature);
            state.revision = worldVersion.Revision;
            return state;
        }

        private static bool IsReadinessCandidate(ReadinessSignals signals) => signals.gameplay_scene &&
            signals.store_present && signals.store_initialized && signals.ui_initialized;

        private Snapshot ResumeMenu()
        {
            // 与 Resume 按钮完全相同的正常入口，不直接 SetActive 或写存档。
            StoreUIManager.Instance.escapeUIManager.OnResume();
            return Capture();
        }

        private IEnumerator Poll()
        {
            while (!stopped)
            {
                bool prepared = false;
                bool recoveryRequired = false;
                try
                {
                    var exchange = new Exchange
                    {
                        schema_version = Protocol.SchemaVersion, session_id = session, broker_epoch = brokerEpoch,
                        state = Capture(), has_result = pendingResult != null, result = pendingResult
                    };
                    current = new UnityWebRequest("http://127.0.0.1:18765/exchange", "POST");
                    current.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(ToJson(exchange)));
                    current.downloadHandler = new DownloadHandlerBuffer();
                    current.SetRequestHeader("Content-Type", "application/json");
                    current.SetRequestHeader("Authorization", "Bearer " + config.token);
                    current.timeout = 2;
                    current.redirectLimit = 0; // 即使本机端点恶意，也不能把凭据带到外网。
                    current.SendWebRequest();
                    prepared = true;
                }
                catch (Exception ex) { log.Warning("PAWNPILOT_PREPARE_FAILED " + ex.GetType().Name); }
                if (prepared)
                {
                    while (!stopped && current != null && !current.isDone) yield return null;
                    if (stopped) yield break;
                    try
                    {
                        if (current.result == UnityWebRequest.Result.Success)
                        {
                            if (current.downloadHandler.data.Length > 65536) throw new InvalidOperationException("Oversize reply");
                            var reply = JsonConvert.DeserializeObject<Reply>(current.downloadHandler.text, JsonSettings);
                            if (reply == null || reply.schema_version != Protocol.SchemaVersion || string.IsNullOrEmpty(reply.broker_epoch))
                                throw new InvalidOperationException("Invalid reply envelope");
                            bool epochChanged = brokerEpoch != null && brokerEpoch != reply.broker_epoch;
                            if (pendingResult == null && !string.IsNullOrEmpty(reply.acknowledged_result_id))
                                throw new InvalidOperationException("Unexpected result acknowledgement");
                            if (pendingResult != null && !string.IsNullOrEmpty(reply.acknowledged_result_id))
                            {
                                if (reply.acknowledged_result_id != pendingResult.id)
                                    throw new InvalidOperationException("Mismatched result acknowledgement");
                                pendingResult = null;
                            }
                            if (pendingResult != null && reply.has_command)
                                throw new InvalidOperationException("Command received before result acknowledgement");
                            if (epochChanged && pendingResult != null && reply.has_command)
                                throw new InvalidOperationException("Broker epoch changed with unresolved result");
                            brokerEpoch = reply.broker_epoch;
                            if (!connectionReported) { log.Log("PAWNPILOT_CONNECTED"); connectionReported = true; }
                            failureReported = false;
                            if (reply.has_command)
                            {
                                if (reply.command == null || reply.command.schema_version != Protocol.SchemaVersion ||
                                    string.IsNullOrEmpty(reply.command.id) || reply.command.broker_epoch != brokerEpoch)
                                    throw new InvalidOperationException("Invalid command envelope");
                                pendingResult = gate.Execute(reply.command, Capture(), brokerEpoch,
                                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), ResumeMenu);
                                log.Log("PAWNPILOT_RESULT " + ToJson(pendingResult));
                            }
                        }
                        else if (current.responseCode == 401 || current.responseCode == 409)
                        {
                            // broker 重启可能遗失已执行动作的确认记录，不能丢弃结果并自动重放。
                            log.Error("PAWNPILOT_RECOVERY_REQUIRED http=" + current.responseCode +
                                "; retained result=" + (pendingResult?.id ?? "none") + "; restart game/mod after reviewing logs");
                            recoveryRequired = true;
                        }
                        else if (!failureReported || connectionReported)
                        {
                            log.Warning("PAWNPILOT_DISCONNECTED http=" + current.responseCode + " error=" + current.error);
                            connectionReported = false;
                            failureReported = true;
                        }
                    }
                    catch (Exception ex) { log.Warning("PAWNPILOT_EXCHANGE_FAILED " + ex.GetType().Name); }
                }
                current?.Dispose(); current = null;
                if (recoveryRequired) yield break;
                // 不依赖 timeScale，暂停菜单内仍可通信。
                yield return new WaitForSecondsRealtime(0.5f);
            }
        }

        public void Shutdown()
        {
            if (stopped) return;
            stopped = true;
            if (subscribed)
            {
                ModHook.OnGameLoadedLate -= OnGameLoaded;
                SceneManager.activeSceneChanged -= SceneChanged;
                subscribed = false;
            }
            StopAllCoroutines();
            current?.Abort(); current?.Dispose(); current = null;
            log?.Log("PAWNPILOT_SHUTDOWN");
        }
        private void OnApplicationQuit() => Shutdown();
        private void OnDestroy() => Shutdown();
    }
}
