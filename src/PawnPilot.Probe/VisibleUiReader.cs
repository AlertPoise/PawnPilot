using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace PawnPilot
{
    // Read-only geometric/text-buffer helpers shared by diagnostic and business readers.
    // These checks do not establish topmost visibility or UI consistency.
    internal static class VisibleUiReader
    {
        internal static string RenderedText(TMP_Text text)
        {
            if (text == null || !text.isActiveAndEnabled || text.color.a <= 0) return null;
            var info = text.textInfo;
            if (text.havePropertiesChanged || info == null || info.characterInfo == null || info.characterCount < 0 ||
                info.characterCount > 256 || info.characterCount > info.characterInfo.Length ||
                info.pageCount > 1 || text.pageToDisplay != 1 || text.isTextOverflowing || text.isTextTruncated ||
                text.maxVisibleCharacters < info.characterCount || text.maxVisibleLines < info.lineCount)
                return null;
            var chars = new char[info.characterCount];
            for (int i = 0; i < chars.Length; i++)
            {
                var character = info.characterInfo[i];
                if (character.pageNumber != 0 || character.lineNumber >= text.maxVisibleLines ||
                    (!char.IsWhiteSpace(character.character) && (!character.isVisible || character.color.a == 0))) return null;
                chars[i] = character.character;
            }
            return new string(chars);
        }

        internal static bool TextVisible(TMP_Text text, RectTransform viewport = null)
        {
            if (text == null || !text.isActiveAndEnabled || text.color.a <= 0 ||
                !RectVisible(text.rectTransform) || RenderedText(text) == null) return false;
            return viewport == null || Contained(text.rectTransform, viewport);
        }

        internal static bool RectVisible(RectTransform rect)
        {
            return RectFailure(rect) == null;
        }

        // Returns a fixed, bounded diagnostic token for the first failed predicate.
        // Keep this in the same order as the original RectVisible checks so callers
        // can localize failures without weakening the visibility policy.
        internal static string RectFailure(RectTransform rect)
        {
            if (rect == null) return "rect_missing";
            if (!rect.gameObject.activeInHierarchy) return "inactive";
            foreach (var group in rect.GetComponentsInParent<CanvasGroup>(false))
                if (group.alpha <= 0) return "canvas_group_transparent";
            foreach (var mask in rect.GetComponentsInParent<RectMask2D>(false))
                if (mask.isActiveAndEnabled && !Contained(rect, mask.rectTransform)) return "rect_mask_clip_unverified";
            // Arbitrary stencil masks are not proven by a bounding rectangle.
            foreach (var mask in rect.GetComponentsInParent<Mask>(false))
                if (mask.isActiveAndEnabled) return "stencil_mask_unverified";
            var canvas = rect.GetComponentInParent<Canvas>();
            if (canvas == null) return "canvas_missing";
            if (!canvas.isActiveAndEnabled) return "canvas_disabled";
            var camera = canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
            if (canvas.renderMode != RenderMode.ScreenSpaceOverlay && camera == null) return "camera_missing";
            var corners = new Vector3[4]; rect.GetWorldCorners(corners);
            foreach (var corner in corners)
            {
                var p = RectTransformUtility.WorldToScreenPoint(camera, corner);
                if (p.x < 0 || p.y < 0 || p.x > Screen.width || p.y > Screen.height) return "corner_offscreen";
            }
            return rect.rect.width > 0 && rect.rect.height > 0 ? null : "nonpositive_rect";
        }

        internal static bool Contained(RectTransform child, RectTransform parent)
        {
            if (child == null || parent == null) return false;
            var corners = new Vector3[4]; child.GetWorldCorners(corners);
            var bounds = parent.rect;
            foreach (var corner in corners)
            {
                var p = parent.InverseTransformPoint(corner);
                if (p.x < bounds.xMin - 0.1f || p.x > bounds.xMax + 0.1f ||
                    p.y < bounds.yMin - 0.1f || p.y > bounds.yMax + 0.1f) return false;
            }
            return true;
        }
    }

    // Compatibility for the opt-in local trade diagnostic. Uses only TMP's existing
    // rendered character buffer; never reads text, reparses markup, or forces a mesh update.
    internal static class DiagnosticRenderedText
    {
        internal static string Read(TMP_Text text) => VisibleUiReader.RenderedText(text);
    }
}
