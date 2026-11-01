using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace PawnPilot
{
    public sealed class ProbeBehaviour : MonoBehaviour
    {
        private ModLog log;
        private SessionConfig config;
        private readonly string session = Guid.NewGuid().ToString("N");
        private readonly ActionGate gate = new ActionGate();
        private ActionDriver actionDriver;
        private readonly WorldVersion worldVersion = new WorldVersion();
        private readonly WorldIdentityTracker worldIdentity = new WorldIdentityTracker();
        private readonly ReadyFrameTracker readyFrames = new ReadyFrameTracker();
        private readonly BusinessProjection businessProjection = new BusinessProjection();
        private GameBusinessActions businessActions;
        private GamePurchaseActions purchaseActions;
        private GameSaleActions saleActions;
        private GameInventoryActions inventoryActions;
        private GameBargainActions bargainActions;
        private GameDayActions dayActions;
        private int? observedDay;
        private string observedDayWorld;
        private UnityWebRequest current;
        private bool stopped, subscribed;
        private int activeSceneHandle = -1;
        private string brokerEpoch;
        private ReadinessSignals latestSignals = new ReadinessSignals();
        private Readiness latestReadiness = GameReadiness.Evaluate(null, null);
        private ActionResult pendingResult;
        private bool connectionReported;
        private bool failureReported;
        private bool driverFailureLogged;
        private static readonly long MonotonicOrigin = Stopwatch.GetTimestamp();
#if M2A_READ_ONLY_PROBE
        private readonly TradeDiagnostic tradeDiagnostic = new TradeDiagnostic();
        private readonly VerificationDiagnostic verificationDiagnostic = new VerificationDiagnostic();
        private string lastVerificationSample;
        private string lastDiagnosticVersion;
        private bool diagnosticStopped;
#endif
        // 使用游戏自带 Json.NET；禁止类型名驱动实例化，限制反序列化深度。
        private static readonly JsonSerializerSettings JsonSettings = new JsonSerializerSettings
        {
            TypeNameHandling = TypeNameHandling.None,
            MetadataPropertyHandling = MetadataPropertyHandling.Ignore,
            MaxDepth = 16,
            Converters = { new ActionArgumentsJsonConverter() }
        };

        private sealed class ActionArgumentsJsonConverter : JsonConverter<ActionArguments>
        {
            public override ActionArguments ReadJson(JsonReader reader, Type objectType, ActionArguments existingValue,
                bool hasExistingValue, JsonSerializer serializer)
            {
                if (reader.TokenType == JsonToken.Null) return null;
                var value = JObject.Load(reader);
                return new ActionArguments
                {
                    customer_ref = value["customer_ref"]?.Value<string>(),
                    item_refs = value["item_refs"]?.ToObject<List<string>>(serializer),
                    offer_ref = value["offer_ref"]?.Value<string>(),
                    collection_fingerprint = value["collection_fingerprint"]?.Value<string>(),
                    option_ref = value["option_ref"]?.Value<string>(),
                    dialogue_ref = value["dialogue_ref"]?.Value<string>(),
                    mode = value["mode"]?.Value<string>(),
                    authorization_ref = value["authorization_ref"]?.Value<string>(),
                    container = value["container"]?.Value<string>(),
                    handoff_ref = value["handoff_ref"]?.Value<string>(),
                    settlement_ref = value["settlement_ref"]?.Value<string>(),
                    amount = value["amount"]?.Value<int>(), max_spend = value["max_spend"]?.Value<int>()
                };
            }

            public override void WriteJson(JsonWriter writer, ActionArguments value, JsonSerializer serializer)
            {
                writer.WriteStartObject();
                if (value != null)
                {
                    WriteString(writer, "customer_ref", value.customer_ref);
                    if (value.item_refs != null)
                    {
                        writer.WritePropertyName("item_refs"); serializer.Serialize(writer, value.item_refs);
                    }
                    WriteString(writer, "offer_ref", value.offer_ref);
                    WriteString(writer, "collection_fingerprint", value.collection_fingerprint);
                    WriteString(writer, "option_ref", value.option_ref);
                    WriteString(writer, "dialogue_ref", value.dialogue_ref);
                    WriteString(writer, "mode", value.mode);
                    WriteString(writer, "authorization_ref", value.authorization_ref);
                    WriteString(writer, "container", value.container);
                    WriteString(writer, "handoff_ref", value.handoff_ref);
                    WriteString(writer, "settlement_ref", value.settlement_ref);
                    if (value.amount.HasValue) { writer.WritePropertyName("amount"); writer.WriteValue(value.amount.Value); }
                    if (value.max_spend.HasValue) { writer.WritePropertyName("max_spend"); writer.WriteValue(value.max_spend.Value); }
                }
                writer.WriteEndObject();
            }

            private static void WriteString(JsonWriter writer, string key, string value)
            {
                if (value == null) return;
                writer.WritePropertyName(key);
                writer.WriteValue(value);
            }
        }
        private static string ToJson(object value) => JsonConvert.SerializeObject(value, JsonSettings);

        public void Initialize(ModLog logger)
        {
            log = logger;
            actionDriver = new ActionDriver(gate);
            businessActions = new GameBusinessActions(businessProjection);
            purchaseActions = new GamePurchaseActions(businessProjection);
            saleActions = new GameSaleActions(businessProjection);
            inventoryActions = new GameInventoryActions(businessProjection);
            bargainActions = new GameBargainActions(businessProjection);
            dayActions = new GameDayActions();
            ModHook.OnGameLoadedLate += OnGameLoaded;
            SceneManager.activeSceneChanged += SceneChanged;
            activeSceneHandle = SceneManager.GetActiveScene().handle;
            subscribed = true;
            StartCoroutine(Bootstrap());
        }

        private void Update()
        {
            if (stopped || actionDriver == null) return;
            var driverStep = actionDriver.Tick(RefreshReadinessFrame, Capture, brokerEpoch,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), MonotonicMilliseconds(),
                InvokeNativeAction, VerifyAction);
            if (driverStep.newlyFailed && !driverFailureLogged)
            {
                driverFailureLogged = true;
                log.Error("PAWNPILOT_EXECUTOR_HALTED " + driverStep.failureType +
                    "; action cancelled fail-closed and no later frame can invoke or complete it");
            }
            if (pendingResult == null && gate.PendingResult != null)
            {
                pendingResult = gate.PendingResult;
                dayActions?.ObserveResult(pendingResult);
                log.Log("PAWNPILOT_RESULT " + ToJson(pendingResult));
            }
#if M2A_READ_ONLY_PROBE
            if (stopped || diagnosticStopped) return;
            try
            {
                var verification = verificationDiagnostic.Capture(worldVersion.Epoch, latestReadiness.ready,
                    VerificationDiagnosticReader.Read);
                var verificationJson = ToJson(new { cash = PlayerStore.HasInstance ? PlayerStore.Instance.playerCash : 0,
                    sample = verification });
                if (verificationJson != lastVerificationSample)
                {
                    log.Log("PAWNPILOT_M2_VERIFY_READ_ONLY " + ToJson(new
                    {
                        source = "game_runtime", diagnostic_format = 1, frame = Time.frameCount,
                        realtime = Time.realtimeSinceStartup, cash = PlayerStore.HasInstance ? PlayerStore.Instance.playerCash : 0,
                        sample = verification
                    }));
                    lastVerificationSample = verificationJson;
                }
                var sample = tradeDiagnostic.Capture(worldVersion.Epoch, latestReadiness.ready, TradeDiagnosticReader.Read);
                string version = sample.world_epoch + ":" + sample.diagnostic_revision;
                if (version == lastDiagnosticVersion) return;
                log.Log("PAWNPILOT_M2A_READ_ONLY " + ToJson(new
                {
                    source = "game_runtime", diagnostic_format = 1, frame = Time.frameCount,
                    realtime = Time.realtimeSinceStartup, sample
                }));
                lastDiagnosticVersion = version;
            }
            catch (Exception)
            {
                // Diagnostic/logging failures must not stop M1 networking or gameplay.
                diagnosticStopped = true;
            }
#endif
        }

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
            var day = ready ? dayActions.Read(worldVersion.Epoch) : null;
            if (observedDayWorld != worldVersion.Epoch)
            {
                observedDayWorld = worldVersion.Epoch;
                observedDay = null;
            }
            if (day?.day_number != null)
            {
                // Native BeginDay retains the run/slot identity. Invalidate short-lived
                // business references without changing the in-flight settlement's world.
                if (observedDay.HasValue && observedDay != day.day_number)
                    businessProjection.Invalidate(worldVersion.Epoch, "day_changed");
                observedDay = day.day_number;
            }
            var state = new Snapshot
            {
                schema_version = Protocol.BusinessSchemaVersion,
                world_epoch = worldVersion.Epoch,
                revision = worldVersion.Revision,
                version = worldVersion.Epoch + ":" + worldVersion.Revision,
                scene = scene.name, loaded = loaded, ready = ready,
                cash = loaded ? PlayerStore.Instance.GetCash() : 0,
                phase = latestReadiness.phase,
                not_ready_reason = latestReadiness.reason,
                menu_open = open,
                can_close_menu = ready && open && menu.isActiveAndEnabled,
                interaction = new InteractionObservation
                {
                    phase = latestReadiness.phase ?? "unknown",
                    consistency = "unverified",
                    reason = ready ? "business_consistency_unverified" : latestReadiness.reason,
                    operable = ready
                },
                business = businessProjection.Capture(worldVersion.Epoch, ready, () => bargainActions.Read(worldVersion.Epoch)),
                legal_actions = new List<LegalAction>()
            };
            state.business.details.day = day;
            state.interaction.consistency = state.business.details.ui.status;
            state.interaction.reason = state.business.details.ui.blockers.Count > 0
                ? state.business.details.ui.blockers[0] : ready ? "business_consistency_unverified" : latestReadiness.reason;
            state.interaction.operable = ready && state.interaction.consistency == "consistent";
            if (state.can_close_menu)
                state.legal_actions.Add(new LegalAction { action = "close_menu", arguments = new ActionArguments() });
            if (ready && !open)
            {
                state.legal_actions.AddRange(businessActions.BuildLegalActions(state));
                state.legal_actions.AddRange(saleActions.BuildLegalActions(state));
                state.legal_actions.AddRange(inventoryActions.BuildLegalActions(state));
                state.legal_actions.AddRange(bargainActions.BuildLegalActions(state));
                state.legal_actions.AddRange(dayActions.BuildLegalActions(state));
                var purchase = purchaseActions.GetLegalAction(state);
                var purchaseCandidate = state.business.details.candidates.Find(candidate => candidate.action == "purchase_visible_offer");
                if (purchaseCandidate != null)
                {
                    purchaseCandidate.status = purchase == null ? "blocked" : "candidate";
                    purchaseCandidate.reason = purchase == null ? purchaseActions.LastUnavailableReason ?? "purchase_preflight_unverified" : "native_preflight_verified";
                }
                if (purchase != null)
                {
                    state.legal_actions.Add(purchase);
                    state.business.details.capacity.status = "sufficient";
                    state.business.details.capacity.reason = "native_grid_placement_verified";
                    state.business.details.capacity.scope = "current_normal_purchase";
                    state.business.details.eligibility.purchase_enabled = true;
                    state.business.details.eligibility.missing.Clear();
                }
                else
                {
                    state.business.details.eligibility.missing = new List<string> { purchaseActions.LastUnavailableReason ?? "purchase_preflight_unverified" };
                    if (purchaseActions.LastUnavailableReason?.StartsWith("capacity_", StringComparison.Ordinal) == true)
                    {
                        state.business.details.capacity.reason = purchaseActions.LastUnavailableReason;
                        state.business.details.capacity.scope = "current_normal_purchase";
                        if (purchaseActions.LastUnavailableReason == "capacity_capacityinsufficient" ||
                            purchaseActions.LastUnavailableReason == "capacity_lockedtarget")
                            state.business.details.capacity.status = "insufficient";
                    }
                }
            }
#if M3C_INSPECTION_TEST
            // Explicit test package only: one existing normal purchase instance, no item mutation.
            businessProjection.TryTriggerTestInspection(state.business);
#endif
            businessProjection.ApplyInspectionGate(state);
            state.interaction.operable = !businessProjection.IsInspectionWaiting && ready && !open &&
                state.interaction.consistency != "blocked" && state.legal_actions.Count > 0;
            state.revision = 0;
            state.version = null;
            string signature = ToJson(state);
            if (Encoding.UTF8.GetByteCount(signature) > 65000)
            {
                state.business = businessProjection.Invalidate(worldVersion.Epoch, "snapshot_size_limit");
                state.interaction.consistency = "unverified";
                state.interaction.reason = "snapshot_size_limit";
                state.interaction.operable = false;
                state.legal_actions.RemoveAll(action => action.action != "close_menu");
                businessProjection.ApplyInspectionGate(state);
                signature = ToJson(state);
            }
            state.version = worldVersion.Observe(signature + "|inventory-sort:" + inventoryActions.CompletionGeneration);
            state.revision = worldVersion.Revision;
            return state;
        }

        private static bool IsReadinessCandidate(ReadinessSignals signals) => signals.gameplay_scene &&
            signals.store_present && signals.store_initialized && signals.ui_initialized;

        private void ResumeMenu()
        {
            // 与 Resume 按钮完全相同的正常入口，不直接 SetActive 或写存档。
            StoreUIManager.Instance.escapeUIManager.OnResume();
        }

        private void InvokeNativeAction(Command command)
        {
            // Refresh immediately before entering any native business operation. Player input stays live.
            var current = Capture();
            if (command.action == "resume_inspection_handoff")
            {
                string rejected = BusinessActionPolicy.Validate(command, current);
                if (rejected != null) throw new NativeActionRejectedException(rejected);
                if (!businessProjection.ResumeInspectionHandoff(command.expected_world_epoch, command.arguments.handoff_ref))
                    throw new NativeActionRejectedException("inspection_handoff_changed");
                return;
            }
            if (businessProjection.IsInspectionWaiting)
                throw new NativeActionRejectedException("inspection_handoff_pending");
            if (command.action == "close_menu") { ResumeMenu(); return; }
            if (command.action == "purchase_visible_offer") { purchaseActions.Invoke(command, current); return; }
            if (command.action == "prepare_sale_items" || command.action == "sell_visible_offer")
            { saleActions.Invoke(command, current); return; }
            if (command.action == "sort_inventory_default") { inventoryActions.Invoke(command, current); return; }
            if (GameBargainActions.Handles(command.action)) { bargainActions.Invoke(command, current); return; }
            if (GameDayActions.Handles(command.action)) { dayActions.Invoke(command, current); return; }
            // The adapter rechecks local native evidence. A wire candidate never enables it.
            businessActions.Invoke(command, current);
        }

        private VerificationDecision VerifyAction(Command command, Snapshot before, Snapshot after) =>
            command.action == "resume_inspection_handoff" ? BusinessActionPolicy.Verify(command, before, after) :
            command.action == "close_menu" ? ActionGate.VerifyCloseMenu(command, before, after) :
            command.action == "purchase_visible_offer" ? purchaseActions.Verify(command, before, after) :
            command.action == "prepare_sale_items" || command.action == "sell_visible_offer" ? saleActions.Verify(command, before, after) :
            command.action == "sort_inventory_default" ? inventoryActions.Verify(command, before, after) :
            GameBargainActions.Handles(command.action) ? bargainActions.Verify(command, before, after) :
            GameDayActions.Handles(command.action) ? dayActions.Verify(command, before, after) :
            businessActions.Verify(command, before, after);

        private static long MonotonicMilliseconds() =>
            (long)((Stopwatch.GetTimestamp() - MonotonicOrigin) * 1000.0 / Stopwatch.Frequency);

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
                        schema_version = Protocol.BusinessSchemaVersion, session_id = session, broker_epoch = brokerEpoch,
                        state = Capture(), has_result = pendingResult != null, result = pendingResult,
                        progress = gate.CurrentProgress
                    };
                    current = new UnityWebRequest("http://127.0.0.1:18765/exchange", "POST");
                    current.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(ToJson(exchange)));
                    current.downloadHandler = new DownloadHandlerBuffer();
                    current.SetRequestHeader("Content-Type", "application/json");
                    current.SetRequestHeader("X-PawnPilot-Wire-Version", "3");
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
                            var reply = DeserializeReplyStrict(current.downloadHandler.text);
                            bool epochChanged = brokerEpoch != null && brokerEpoch != reply.broker_epoch;
                            if (pendingResult == null && !string.IsNullOrEmpty(reply.acknowledged_result_id))
                                throw new InvalidOperationException("Unexpected result acknowledgement");
                            if (pendingResult != null && !string.IsNullOrEmpty(reply.acknowledged_result_id))
                            {
                                if (reply.acknowledged_result_id != pendingResult.id)
                                    throw new InvalidOperationException("Mismatched result acknowledgement");
                                if (!gate.Acknowledge(reply.acknowledged_result_id))
                                    throw new InvalidOperationException("Unknown result acknowledgement");
                                pendingResult = null;
                            }
                            if (epochChanged)
                            {
                                var epochResult = gate.OnBrokerEpochChanged(reply.broker_epoch, Capture());
                                if (pendingResult == null && epochResult != null) pendingResult = gate.PendingResult ?? epochResult;
                            }
                            if ((pendingResult != null || gate.IsWriteBlocked) && reply.has_command)
                                throw new InvalidOperationException("Command received before result acknowledgement");
                            brokerEpoch = reply.broker_epoch;
                            if (!connectionReported) { log.Log("PAWNPILOT_CONNECTED"); connectionReported = true; }
                            failureReported = false;
                            if (reply.has_command)
                            {
                                if (actionDriver.IsFailed)
                                {
                                    log.Error("PAWNPILOT_RECOVERY_REQUIRED command received after executor halt; action not accepted");
                                    recoveryRequired = true;
                                }
                                else
                                {
                                var immediate = gate.Accept(reply.command, Capture(), brokerEpoch,
                                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                                if (immediate != null)
                                {
                                    var retained = gate.PendingResult;
                                    if (retained != null && retained.id == immediate.id)
                                    {
                                        pendingResult = retained;
                                        log.Log("PAWNPILOT_RESULT " + ToJson(pendingResult));
                                    }
                                    else
                                    {
                                        log.Error("PAWNPILOT_PROTOCOL_REJECTED untracked command result id=" + (immediate.id ?? "none") +
                                            " reason=" + immediate.reason + "; restart game/mod after reviewing logs");
                                        recoveryRequired = true;
                                    }
                                }
                                }
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

        private static Reply DeserializeReplyStrict(string json)
        {
            using (var sr = new System.IO.StringReader(json))
            using (var reader = new JsonTextReader(sr) { MaxDepth = 16, DateParseHandling = DateParseHandling.None })
            {
                var root = JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
                if (reader.Read()) throw new InvalidOperationException("Trailing JSON content");
                RequireKeys(root, "schema_version", "broker_epoch", "acknowledged_result_id", "has_command", "command");
                RequireInteger(root, "schema_version", Protocol.BusinessSchemaVersion, Protocol.BusinessSchemaVersion);
                RequireBoundedString(root, "broker_epoch", 128, false);
                RequireString(root, "acknowledged_result_id", true);
                if (root["acknowledged_result_id"].Type == JTokenType.String)
                {
                    int ackLength = root["acknowledged_result_id"].Value<string>().Length;
                    if (ackLength < 1 || ackLength > 128) throw new InvalidOperationException("Invalid acknowledgement id");
                }
                RequireType(root, "has_command", JTokenType.Boolean);
                bool hasCommand = root["has_command"].Value<bool>();
                if (hasCommand)
                {
                    if (!(root["command"] is JObject command)) throw new InvalidOperationException("command must be object");
                    RequireKeys(command, "schema_version", "id", "broker_epoch", "action", "expected_world_epoch",
                        "expected_revision", "expected_version", "arguments", "start_deadline_unix_ms", "execution_timeout_ms");
                    RequireInteger(command, "schema_version", Protocol.BusinessSchemaVersion, Protocol.BusinessSchemaVersion);
                    RequireBoundedString(command, "id", 128, false);
                    RequireBoundedString(command, "broker_epoch", 128, false);
                    RequireBoundedString(command, "action", 128, false);
                    RequireBoundedString(command, "expected_world_epoch", 128, false);
                    RequireInteger(command, "expected_revision", 0, int.MaxValue);
                    RequireBoundedString(command, "expected_version", 256, false);
                    RequireInteger(command, "start_deadline_unix_ms", 1, long.MaxValue);
                    RequireInteger(command, "execution_timeout_ms", 1, 30000);
                    if (!(command["arguments"] is JObject arguments)) throw new InvalidOperationException("arguments must be object");
                    RequireAllowedOptionalKeys(arguments, "customer_ref", "item_refs", "offer_ref", "collection_fingerprint", "option_ref", "dialogue_ref", "mode", "amount", "max_spend", "authorization_ref", "container", "handoff_ref", "settlement_ref");
                    foreach (var key in new[] { "customer_ref", "offer_ref", "collection_fingerprint", "option_ref", "dialogue_ref", "mode", "authorization_ref", "container", "handoff_ref", "settlement_ref" })
                        if (arguments.Property(key) != null) RequireString(arguments, key, false);
                    foreach (var key in new[] { "amount", "max_spend" })
                        if (arguments.Property(key) != null) RequireInteger(arguments, key,
                            key == "amount" && command["action"]?.Value<string>() == "confirm_end_of_day" ? int.MinValue : 0, int.MaxValue);
                    if (arguments.Property("item_refs") != null)
                    {
                        if (!(arguments["item_refs"] is JArray items)) throw new InvalidOperationException("item_refs must be array");
                        foreach (var item in items) if (item.Type != JTokenType.String) throw new InvalidOperationException("item_refs must contain strings");
                    }
                }
                else if (root["command"].Type != JTokenType.Null) throw new InvalidOperationException("command must be null without has_command");
                var reply = root.ToObject<Reply>(JsonSerializer.Create(JsonSettings));
                if (reply == null || reply.schema_version != Protocol.BusinessSchemaVersion || string.IsNullOrEmpty(reply.broker_epoch))
                    throw new InvalidOperationException("Invalid reply envelope");
                return reply;
            }
        }

        private static void RequireKeys(JObject value, params string[] required)
        {
            var expected = new HashSet<string>(required, StringComparer.Ordinal);
            foreach (var property in value.Properties()) if (!expected.Remove(property.Name)) throw new InvalidOperationException("Unexpected JSON key");
            if (expected.Count != 0) throw new InvalidOperationException("Missing JSON key");
        }

        private static void RequireAllowedOptionalKeys(JObject value, params string[] allowed)
        {
            var expected = new HashSet<string>(allowed, StringComparer.Ordinal);
            foreach (var property in value.Properties()) if (!expected.Contains(property.Name)) throw new InvalidOperationException("Unexpected JSON key");
        }

        private static void RequireType(JObject value, string key, JTokenType type)
        {
            if (value[key] == null || value[key].Type != type) throw new InvalidOperationException("Invalid JSON value type");
        }

        private static void RequireString(JObject value, string key, bool allowNull)
        {
            var token = value[key];
            if (token == null || (token.Type != JTokenType.String && !(allowNull && token.Type == JTokenType.Null)))
                throw new InvalidOperationException("Invalid JSON string");
        }

        private static void RequireBoundedString(JObject value, string key, int maxLength, bool allowNull)
        {
            RequireString(value, key, allowNull);
            if (value[key].Type == JTokenType.String)
            {
                int length = value[key].Value<string>().Length;
                if (length < 1 || length > maxLength) throw new InvalidOperationException("String outside bounds");
            }
        }

        private static void RequireInteger(JObject value, string key, long minimum, long maximum)
        {
            var token = value[key];
            if (token == null || token.Type != JTokenType.Integer) throw new InvalidOperationException("Invalid JSON integer");
            long number;
            try { number = token.Value<long>(); }
            catch { throw new InvalidOperationException("Integer outside range"); }
            if (number < minimum || number > maximum) throw new InvalidOperationException("Integer outside range");
        }

        public void Shutdown()
        {
            if (stopped) return;
            dayActions?.Dispose();
            try
            {
                var cancelled = gate.Cancel("mod_shutdown", null);
                if (cancelled != null)
                    log?.Warning("PAWNPILOT_LOCAL_CANCEL status=" + cancelled.status + " reason=" + cancelled.reason +
                        "; shutdown may disconnect before this result is acknowledged");
            }
            catch (Exception ex) { log?.Warning("PAWNPILOT_LOCAL_CANCEL_FAILED " + ex.GetType().Name); }
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
