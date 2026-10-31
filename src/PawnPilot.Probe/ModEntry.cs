using System;
using UnityEngine;

namespace PawnPilot
{
    public sealed class ModEntry : IMod
    {
        private ModLog log;
        private GameObject host;
        private bool compatible;
        public void Init(ModManifest manifest)
        {
            log = new ModLog(manifest);
            // 不把商店版本号、Unity Application.version 与 Steam build 混为一谈。
            compatible = Application.version == "0.46D" && PlayerStore.VERSION == "VERSION 100-Rev6";
            log.Log("PAWNPILOT_INIT app=" + Application.version + " content=" + PlayerStore.VERSION);
            if (!compatible) log.Error("PAWNPILOT_INCOMPATIBLE: tested only against Steam build 26337366 / 100-Rev6");
        }
        public void OnEnable()
        {
            if (!compatible || host != null) return;
            try
            {
                host = new GameObject("PawnPilot Game Bridge");
                UnityEngine.Object.DontDestroyOnLoad(host);
                host.AddComponent<ProbeBehaviour>().Initialize(log);
                log.Log("PAWNPILOT_ENABLED");
            }
            catch (Exception ex)
            {
                log.Error("PAWNPILOT_INITIALIZATION_FAILED " + ex.GetType().Name);
                OnDisable();
            }
        }
        public void OnDisable()
        {
            if (host != null)
            {
                host.GetComponent<ProbeBehaviour>()?.Shutdown();
                UnityEngine.Object.Destroy(host);
                host = null;
            }
            log?.Log("PAWNPILOT_DISABLED");
        }
    }
}
