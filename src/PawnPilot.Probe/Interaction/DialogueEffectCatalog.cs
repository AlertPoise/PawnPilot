using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;

namespace PawnPilot
{
    // 只审核回调身份与捕获对象，不调用委托，不读取/输出未来台词。
    // 新剧情复用无回调/已审核效果即可使用组件；未知经济回调保留为不可执行。
    internal static class DialogueEffectCatalog
    {
        private static readonly bool SupportedAssembly = CheckAssembly();
        internal static bool VersionVerified => SupportedAssembly;

        internal static string Inspect(DialogUIManager manager, StoreClient client, Dialogue selected = null, Action choice = null)
        {
            if (!SupportedAssembly || manager == null || client == null) return "unknown";
            bool gift = false;
            var seen = new HashSet<Dialogue>();
            if (!InspectCallback(choice, client, ref gift)) return "unknown";
            // 先前已排队的行可能被正常输入刷新；必须计入同一效果边界。
            var queued = ReadField(manager, "scheduledEndActionLines") as IEnumerable;
            if (queued == null) return "unknown";
            foreach (var value in queued)
                if (value is Dialogue line && !line.isEndActionExecuted && !InspectCallback(line.endAction, client, ref gift))
                    return "unknown";
            if (manager.currentDialog != null && !manager.currentDialog.isEndActionExecuted &&
                !InspectCallback(manager.currentDialog.endAction, client, ref gift)) return "unknown";
            var current = selected ?? manager.currentDialog;
            while (current != null)
            {
                if (!seen.Add(current) || seen.Count > 64) return "unknown";
                if (current.isDisabledFunc != null || current.isHiddenFunc != null) return "unknown";
                if (!InspectCallback(current.startAction, client, ref gift) ||
                    (!current.isEndActionExecuted && !InspectCallback(current.endAction, client, ref gift))) return "unknown";
                if (current.choices == null || current.choices.Count > 16) return "unknown";
                // 只审核原生显示/选中检查会调用的predicate，绝不执行predicate探测。
                foreach (var option in current.choices)
                    if (option.Item2 == null || option.Item2.isDisabledFunc != null || option.Item2.isHiddenFunc != null)
                        return "unknown";
                if (current.choices.Count > 0) break; // 到下一选择边界；尚未选中的callback不执行。
                current = current.nextDialogue;
            }
            return gift ? "document_gift" : "presentation";
        }

        private static bool InspectCallback(Action callback, StoreClient client, ref bool gift)
        {
            if (callback == null) return true;
            if (callback.GetInvocationList().Length != 1 || callback.Method.Module != typeof(Dialogue).Module) return false;
            var method = callback.Method;
            // 完全空方法的IL不可能产生外部效果；不以方法名猜测语义。
            byte[] code = method.GetMethodBody()?.GetILAsByteArray();
            if (code != null && code.Length > 0 && code[code.Length - 1] == 0x2a)
            {
                bool empty = true;
                for (int i = 0; i < code.Length - 1; i++) if (code[i] != 0) empty = false;
                if (empty) return true;
            }
            // 已按目标SHA核验的“当前客户可遣走”字段赋值；闭包必须指向当前客户。
            if (method.MetadataToken == 0x060055ae && callback.Target != null)
                return ReferenceEquals(ReadField(callback.Target, "storeClient"), client);
            // 已审核复合效果：最多一个自有文档入前柜(可放置失败)+服务剧情状态。
            // 这里只登记效果契约，不根据NPC identifier选择执行器。
            if (method.MetadataToken == 0x0600557a && callback.Target != null &&
                callback.Target.GetType() == method.DeclaringType &&
                method.DeclaringType.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Length == 0)
            { gift = true; return true; }
            return false;
        }

        internal static object ReadField(object target, string name)
        {
            if (target == null) return null;
            var field = target.GetType().GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (field == null) throw new InvalidOperationException("dialogue_field_unavailable");
            return field.GetValue(target);
        }

        private static bool CheckAssembly()
        {
            try
            {
                // MVID已与本机SHA-256核验结果绑定；读取已载入元数据，不扩大files权限。
                return typeof(Dialogue).Module.ModuleVersionId == new Guid("83045710-adce-4e78-8ff1-b43a9399ec0d");
            }
            catch { return false; }
        }
    }
}
