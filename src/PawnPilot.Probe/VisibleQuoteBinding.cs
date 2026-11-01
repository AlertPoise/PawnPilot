using System;
using System.Reflection;

namespace PawnPilot
{
    /// <summary>
    /// Reads the item reference from the two known negotiation-row callback closures.
    /// This does not invoke the callback and does not establish quote freshness.
    /// </summary>
    internal static class VisibleQuoteBinding
    {
        private const string ItemRowClosureFullName = "NegociationUIManager+<>c__DisplayClass80_0";
        private const string ExpandedRowClosureFullName = "NegociationUIManager+<>c__DisplayClass88_0";
        private const string ItemRowCallbackName = "<BuildItemRow>b__1";
        private const string ExpandedRowCallbackName = "<BuildExpandedRow>b__0";

        internal static bool TryGetItem(TabletListRow row, out GameItem item)
        {
            item = null;
            if (row == null)
                return false;

            try
            {
                Action callback = row.onClick;
                if (callback == null || callback.GetInvocationList().Length != 1)
                    return false;

                MethodInfo method = callback.Method;
                object target = callback.Target;
                if (method == null || target == null || method.IsStatic || !method.IsAssembly ||
                    method.ReturnType != typeof(void) || method.GetParameters().Length != 0)
                    return false;

                Type closureType = method.DeclaringType;
                if (closureType == null || !ReferenceEquals(target.GetType(), closureType))
                    return false;

                Assembly gameAssembly = typeof(NegociationUIManager).Assembly;
                if (!ReferenceEquals(closureType.Assembly, gameAssembly) ||
                    !ReferenceEquals(method.Module.Assembly, gameAssembly))
                    return false;

                bool exactPair =
                    string.Equals(closureType.FullName, ItemRowClosureFullName, StringComparison.Ordinal) &&
                    string.Equals(method.Name, ItemRowCallbackName, StringComparison.Ordinal)
                    || (string.Equals(closureType.FullName, ExpandedRowClosureFullName, StringComparison.Ordinal) &&
                        string.Equals(method.Name, ExpandedRowCallbackName, StringComparison.Ordinal));
                // Keep the method/closure pair exact; only the single known data field is read.
                if (!exactPair)
                    return false;

                FieldInfo itemField = closureType.GetField("item",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
                if (itemField == null || !itemField.IsPublic || itemField.IsStatic ||
                    !ReferenceEquals(itemField.DeclaringType, closureType) ||
                    !ReferenceEquals(itemField.FieldType, typeof(GameItem)))
                    return false;

                object value = itemField.GetValue(target);
                if (ReferenceEquals(value, null))
                    return false;
                GameItem boundItem = value as GameItem;
                if (ReferenceEquals(boundItem, null))
                    return false;

                item = boundItem;
                return true;
            }
            catch
            {
                item = null;
                return false;
            }
        }
    }
}
