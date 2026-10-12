// Layout Manager: dock editor windows without drag-and-drop (for tiling WMs where Unity's
// drag-to-dock can't work). Drives Unity's own internal docking code via reflection.
// Unity 6+ only. Verified against the internals of 6000.3, 6000.4 and 6000.6.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.ShortcutManagement;
using UnityEngine;

namespace LayoutManagerTool
{
    // Values match UnityEditor.SplitView.ViewEdge.
    public enum DockEdge { Tab = 0, Left = 1, Bottom = 2, Top = 4, Right = 8 }

    public class Pane
    {
        public ScriptableObject dock;
        public Rect rect;
        public List<EditorWindow> tabs;
        public int selected;
    }

    public class Container
    {
        public ScriptableObject window;
        public bool main;
        public Rect rect;
        public List<Pane> panes = new List<Pane>();
    }

    public static class Docking
    {
        const BindingFlags I = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        const BindingFlags S = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        static readonly Assembly A = typeof(EditorWindow).Assembly;
        static readonly Type tView = A.GetType("UnityEditor.View");
        static readonly Type tDock = A.GetType("UnityEditor.DockArea");
        static readonly Type tSplit = A.GetType("UnityEditor.SplitView");
        static readonly Type tContainer = A.GetType("UnityEditor.ContainerWindow");
        static readonly Type tDropInfo = A.GetType("UnityEditor.DropInfo");
        static readonly Type tExtra = A.GetType("UnityEditor.SplitView+ExtraDropInfo");
        static readonly Type tEdge = A.GetType("UnityEditor.SplitView+ViewEdge");

        static readonly FieldInfo fParent = typeof(EditorWindow).GetField("m_Parent", I);
        static readonly FieldInfo fPanes = tDock?.GetField("m_Panes", I);
        static readonly FieldInfo fSelected = tDock?.GetField("m_Selected", I);
        static readonly FieldInfo fDragSource = tDock?.GetField("s_OriginalDragSource", S);
        static readonly FieldInfo fUserData = tDropInfo?.GetField("userData", I);
        static readonly FieldInfo fRect = tDropInfo?.GetField("rect", I);
        static readonly PropertyInfo pParent = tView?.GetProperty("parent", I);
        static readonly PropertyInfo pWindow = tView?.GetProperty("window", I);
        static readonly PropertyInfo pChildren = tView?.GetProperty("children", I);
        static readonly PropertyInfo pScreenPos = tView?.GetProperty("screenPosition", I);
        static readonly PropertyInfo pShowMode = tContainer?.GetProperty("showMode", I);
        static readonly PropertyInfo pRootView = tContainer?.GetProperty("rootView", I);
        static readonly PropertyInfo pRootSplit = tContainer?.GetProperty("rootSplitView", I);
        static readonly MethodInfo mDockDrop = tDock?.GetMethod("PerformDrop", I);
        static readonly MethodInfo mSplitDrop = tSplit?.GetMethod("PerformDrop", I);
        static readonly MethodInfo mIndexOf = tView?.GetMethod("IndexOfChild", I);
        static readonly MethodInfo mResetDrag = tDock?.GetMethod("ResetDragVars", S);

        // Non-null when this Unity version's internals don't match what we drive.
        public static readonly string Error = FindMissing();

        static string FindMissing()
        {
            var members = new (string name, object m)[]
            {
                ("View", tView), ("DockArea", tDock), ("SplitView", tSplit), ("ContainerWindow", tContainer),
                ("DropInfo", tDropInfo), ("SplitView.ExtraDropInfo", tExtra), ("SplitView.ViewEdge", tEdge),
                ("EditorWindow.m_Parent", fParent), ("DockArea.m_Panes", fPanes), ("DockArea.m_Selected", fSelected),
                ("DockArea.s_OriginalDragSource", fDragSource), ("DropInfo.userData", fUserData), ("DropInfo.rect", fRect),
                ("View.parent", pParent), ("View.window", pWindow), ("View.children", pChildren),
                ("View.screenPosition", pScreenPos),
                ("ContainerWindow.showMode", pShowMode), ("ContainerWindow.rootView", pRootView),
                ("ContainerWindow.rootSplitView", pRootSplit), ("DockArea.PerformDrop", mDockDrop),
                ("SplitView.PerformDrop", mSplitDrop), ("View.IndexOfChild", mIndexOf), ("DockArea.ResetDragVars", mResetDrag),
            };
            var missing = members.Where(x => x.m == null).Select(x => x.name).ToArray();
            if (missing.Length == 0) return null;
            var msg = "Layout Manager: unsupported Unity version, missing internals: " + string.Join(", ", missing);
            Debug.LogError(msg);
            return msg;
        }

        public static ScriptableObject DockOf(EditorWindow w) =>
            w != null && Error == null && fParent.GetValue(w) is ScriptableObject p && tDock.IsInstanceOfType(p) ? p : null;

        public static List<EditorWindow> Tabs(ScriptableObject dock) =>
            ((IList)fPanes.GetValue(dock)).Cast<EditorWindow>().Where(w => w != null).ToList();

        public static List<Container> Snapshot()
        {
            var result = new List<Container>();
            if (Error != null) return result;
            // ContainerWindow.windows comes back empty on Linux, so find them directly.
            foreach (ScriptableObject cw in Resources.FindObjectsOfTypeAll(tContainer))
            {
                if (cw == null || !(pRootView.GetValue(cw) is object root)) continue;
                var c = new Container { window = cw, main = Convert.ToInt32(pShowMode.GetValue(cw)) == 4 }; // ShowMode.MainWindow
                Collect(root, c.panes);
                if (c.panes.Count == 0) continue; // popups, tooltips, utility windows
                c.rect = c.panes.Select(p => p.rect).Aggregate((a, b) => Rect.MinMaxRect(
                    Mathf.Min(a.xMin, b.xMin), Mathf.Min(a.yMin, b.yMin), Mathf.Max(a.xMax, b.xMax), Mathf.Max(a.yMax, b.yMax)));
                result.Add(c);
            }
            return result;
        }

        static void Collect(object view, List<Pane> panes)
        {
            if (tDock.IsInstanceOfType(view))
            {
                var dock = (ScriptableObject)view;
                panes.Add(new Pane { dock = dock, rect = (Rect)pScreenPos.GetValue(dock), tabs = Tabs(dock), selected = (int)fSelected.GetValue(dock) });
                return;
            }
            foreach (var child in (Array)pChildren.GetValue(view)) Collect(child, panes);
        }

        // Moves w into target: as a tab, or split off one edge of the target pane
        // (or of the target's whole window when windowEdge). Returns an error message or null.
        // Mutates the view tree, so never call it from inside OnGUI; use DockLater there.
        public static string Dock(EditorWindow w, ScriptableObject target, DockEdge edge, bool windowEdge = false)
        {
            if (Error != null) return Error;
            var source = DockOf(w);
            if (source == null) return $"'{(w ? w.titleContent.text : "window")}' can't be docked (utility/popup window).";
            if (target == null) return "No target pane.";
            if (edge != DockEdge.Tab && !windowEdge && source == target && Tabs(source).Count == 1)
                return null; // splitting a lone tab off its own pane is a no-op

            try
            {
                // Unity's drop code removes the window from whatever pane the drag started in.
                fDragSource.SetValue(null, source);
                if (edge == DockEdge.Tab)
                {
                    mDockDrop.Invoke(target, new object[] { w, null, Vector2.zero });
                    return null;
                }

                var split = windowEdge ? pRootSplit.GetValue(pWindow.GetValue(target)) : pParent.GetValue(target);
                if (split == null || !tSplit.IsInstanceOfType(split)) return "Target pane isn't inside a split view.";
                int index = windowEdge ? 0 : (int)mIndexOf.Invoke(split, new[] { target });
                var info = Activator.CreateInstance(tDropInfo, split);
                fUserData.SetValue(info, Activator.CreateInstance(tExtra, windowEdge, Enum.ToObject(tEdge, (int)edge), index));
                // Screen-space rect of the new pane; Unity sizes the split from it.
                fRect.SetValue(info, Slice((Rect)pScreenPos.GetValue(windowEdge ? split : target), edge, windowEdge ? 0.25f : 0.5f));
                mSplitDrop.Invoke(split, new[] { w, info, Vector2.zero });
                return null;
            }
            catch (TargetInvocationException e)
            {
                Debug.LogException(e.InnerException);
                return "Unity's docking code threw; see the exception above.";
            }
            finally
            {
                mResetDrag.Invoke(null, null);
            }
        }

        public static void DockLater(EditorWindow w, ScriptableObject target, DockEdge edge, bool windowEdge = false) =>
            EditorApplication.delayCall += () => Report(Dock(w, target, edge, windowEdge));

        public static void Report(string error)
        {
            if (error != null) Debug.LogWarning(error.StartsWith("Layout Manager") ? error : "Layout Manager: " + error);
        }

        public static Rect Slice(Rect r, DockEdge edge, float f)
        {
            switch (edge)
            {
                case DockEdge.Left: return new Rect(r.x, r.y, r.width * f, r.height);
                case DockEdge.Right: return new Rect(r.xMax - r.width * f, r.y, r.width * f, r.height);
                case DockEdge.Top: return new Rect(r.x, r.y, r.width, r.height * f);
                case DockEdge.Bottom: return new Rect(r.x, r.yMax - r.height * f, r.width, r.height * f);
                default: return r;
            }
        }

        // Moves every tab out of floating windows into the biggest pane of the main window.
        public static void GatherFloating()
        {
            var all = Snapshot();
            var target = all.Where(c => c.main).SelectMany(c => c.panes)
                .OrderByDescending(p => p.rect.width * p.rect.height).FirstOrDefault();
            if (target == null) { Report("No main window pane found."); return; }
            foreach (var w in all.Where(c => !c.main).SelectMany(c => c.panes).SelectMany(p => p.tabs).ToList())
                Report(Dock(w, target.dock, DockEdge.Tab));
        }
    }

    // Remembers the previously focused window: the target for keyboard docking.
    // Workflow: click the target window, click the window to move, press a shortcut.
    [InitializeOnLoad]
    public static class FocusTracker
    {
        static EditorWindow previous, current;

        static FocusTracker()
        {
            EditorWindow.windowFocusChanged += () =>
            {
                var f = EditorWindow.focusedWindow;
                if (f == null || f == current || f is LayoutManagerWindow) return;
                previous = current;
                current = f;
            };
        }

        public static EditorWindow TargetFor(EditorWindow moving) => moving == current ? previous : current;
    }

    public static class LayoutShortcuts
    {
        const ShortcutModifiers AS = ShortcutModifiers.Alt | ShortcutModifiers.Shift;
        const ShortcutModifiers CAS = ShortcutModifiers.Action | ShortcutModifiers.Alt | ShortcutModifiers.Shift;

        [Shortcut("Layout Manager/Open", KeyCode.L, AS)]
        static void Open() => LayoutManagerWindow.Open();

        [Shortcut("Layout Manager/Dock As Tab Of Previous", KeyCode.Space, AS)]
        static void Tab() => DockFocused(DockEdge.Tab, false);
        [Shortcut("Layout Manager/Dock Left Of Previous", KeyCode.LeftArrow, AS)]
        static void Left() => DockFocused(DockEdge.Left, false);
        [Shortcut("Layout Manager/Dock Right Of Previous", KeyCode.RightArrow, AS)]
        static void Right() => DockFocused(DockEdge.Right, false);
        [Shortcut("Layout Manager/Dock Above Previous", KeyCode.UpArrow, AS)]
        static void Up() => DockFocused(DockEdge.Top, false);
        [Shortcut("Layout Manager/Dock Below Previous", KeyCode.DownArrow, AS)]
        static void Down() => DockFocused(DockEdge.Bottom, false);

        [Shortcut("Layout Manager/Dock To Window Left Edge", KeyCode.LeftArrow, CAS)]
        static void WinLeft() => DockFocused(DockEdge.Left, true);
        [Shortcut("Layout Manager/Dock To Window Right Edge", KeyCode.RightArrow, CAS)]
        static void WinRight() => DockFocused(DockEdge.Right, true);
        [Shortcut("Layout Manager/Dock To Window Top Edge", KeyCode.UpArrow, CAS)]
        static void WinUp() => DockFocused(DockEdge.Top, true);
        [Shortcut("Layout Manager/Dock To Window Bottom Edge", KeyCode.DownArrow, CAS)]
        static void WinDown() => DockFocused(DockEdge.Bottom, true);

        [Shortcut("Layout Manager/Gather Floating Windows")]
        static void Gather() => EditorApplication.delayCall += Docking.GatherFloating;

        static void DockFocused(DockEdge edge, bool windowEdge)
        {
            var w = EditorWindow.focusedWindow;
            var target = Docking.DockOf(FocusTracker.TargetFor(w));
            if (w == null || target == null)
            {
                Docking.Report("click the target window, then the window to move, then press the shortcut.");
                return;
            }
            Docking.DockLater(w, target, edge, windowEdge);
        }
    }

    public class LayoutManagerWindow : EditorWindow
    {
        const float FloatRowHeight = 140, Margin = 14;
        static readonly Color Accent = new Color(0.25f, 0.55f, 1f);

        EditorWindow picked;

        [MenuItem("Window/Layout Manager")]
        public static void Open() => GetWindow<LayoutManagerWindow>("Layout Manager");

        void OnEnable() => wantsMouseMove = true;
        void OnInspectorUpdate() => Repaint(); // the layout can change behind our back

        void OnGUI()
        {
            if (Docking.Error != null) { EditorGUILayout.HelpBox(Docking.Error, MessageType.Error); return; }
            if (picked == null) picked = null; // drop a destroyed window's fake-null reference
            if (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Escape) { picked = null; Event.current.Use(); }

            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                GUILayout.Label(picked == null
                    ? "Click a tab to pick it up. Right-click for more."
                    : $"Moving '{picked.titleContent.text}': click a zone (center = tab, edges = split, outer border = window edge). Esc cancels.");
                GUILayout.FlexibleSpace();
                if (picked != null && GUILayout.Button("Cancel", EditorStyles.toolbarButton)) picked = null;
                if (GUILayout.Button("Gather Floating", EditorStyles.toolbarButton)) EditorApplication.delayCall += Docking.GatherFloating;
                if (GUILayout.Button("Shortcuts…", EditorStyles.toolbarButton)) EditorApplication.ExecuteMenuItem("Edit/Shortcuts...");
            }

            var area = GUILayoutUtility.GetRect(0, 0, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
            var all = Docking.Snapshot();
            var floats = all.Where(c => !c.main).ToList();
            var mainArea = floats.Count == 0 ? area : new Rect(area.x, area.y, area.width, area.height - FloatRowHeight);
            foreach (var c in all.Where(c => c.main)) DrawContainer(c, mainArea, "Main Window");
            for (int i = 0; i < floats.Count; i++)
            {
                float w = area.width / floats.Count;
                DrawContainer(floats[i], new Rect(area.x + i * w, area.yMax - FloatRowHeight, w, FloatRowHeight), "Floating");
            }
            if (Event.current.type == EventType.MouseMove) Repaint();
        }

        void DrawContainer(Container c, Rect area, string label)
        {
            if (c.rect.width < 1 || c.rect.height < 1) return;
            area = new Rect(area.x + 4, area.y + 4, area.width - 8, area.height - 8);
            GUI.Label(new Rect(area.x, area.y, area.width, 16), label, EditorStyles.miniBoldLabel);
            var box = new Rect(area.x, area.y + 16, area.width, area.height - 16);
            EditorGUI.DrawRect(box, new Color(0, 0, 0, 0.12f));

            // Fit the window's screen rect into the box, keeping aspect, leaving a margin for window-edge zones.
            var inner = new Rect(box.x + Margin, box.y + Margin, box.width - 2 * Margin, box.height - 2 * Margin);
            float scale = Mathf.Min(inner.width / c.rect.width, inner.height / c.rect.height);
            var size = c.rect.size * scale;
            var origin = inner.center - size / 2;
            Func<Rect, Rect> Map = r => new Rect(origin + (r.position - c.rect.position) * scale, r.size * scale);

            var e = Event.current;
            var mapped = new Rect(origin, size);
            Pane hoverPane = null;
            DockEdge hoverEdge = DockEdge.Tab;
            bool windowEdge = false;
            if (picked != null && box.Contains(e.mousePosition))
            {
                hoverPane = c.panes.FirstOrDefault(p => Map(p.rect).Contains(e.mousePosition));
                if (hoverPane != null) hoverEdge = ZoneAt(Map(hoverPane.rect), e.mousePosition);
                else
                {
                    // Mouse is in the margin: dock to that edge of the whole window.
                    windowEdge = true;
                    hoverPane = c.panes[0];
                    hoverEdge = NearestEdge(mapped, e.mousePosition);
                }
            }

            if (hoverPane != null && e.type == EventType.MouseDown && e.button == 0)
            {
                Docking.DockLater(picked, hoverPane.dock, hoverEdge, windowEdge);
                picked = null;
                e.Use();
            }

            foreach (var p in c.panes) DrawPane(p, Map(p.rect));

            if (hoverPane != null && e.type == EventType.Repaint)
            {
                var preview = windowEdge ? Docking.Slice(mapped, hoverEdge, 0.25f) : Docking.Slice(Map(hoverPane.rect), hoverEdge, 0.5f);
                EditorGUI.DrawRect(preview, new Color(Accent.r, Accent.g, Accent.b, 0.35f));
                Outline(preview, Accent, 2);
            }
        }

        void DrawPane(Pane p, Rect r)
        {
            EditorGUI.DrawRect(r, new Color(0.5f, 0.5f, 0.5f, 0.15f));
            Outline(r, p.tabs.Contains(picked) ? Accent : new Color(0.5f, 0.5f, 0.5f, 0.8f), p.tabs.Contains(picked) ? 2 : 1);

            const float h = 18;
            var row = new Rect(r.x + 3, r.y + 3, r.width - 6, h);
            for (int i = 0; i < p.tabs.Count; i++, row.y += h + 2)
            {
                if (row.yMax > r.yMax - 2)
                {
                    GUI.Label(row, $"+{p.tabs.Count - i} more", EditorStyles.miniLabel);
                    break;
                }
                var w = p.tabs[i];
                var content = new GUIContent(w.titleContent.text, w.titleContent.image, w.GetType().FullName);
                var style = i == p.selected ? EditorStyles.miniButtonMid : EditorStyles.miniButton;
                if (Event.current.type == EventType.MouseDown && Event.current.button == 1 && row.Contains(Event.current.mousePosition))
                {
                    TabMenu(w);
                    Event.current.Use();
                }
                var prev = GUI.backgroundColor;
                if (w == picked) GUI.backgroundColor = Accent;
                if (GUI.Button(row, content, style)) picked = w;
                GUI.backgroundColor = prev;
            }
        }

        void TabMenu(EditorWindow w)
        {
            var menu = new GenericMenu();
            menu.AddItem(new GUIContent("Focus"), false, () => w.Focus());
            menu.AddItem(new GUIContent("Pick Up"), false, () => picked = w);
            menu.AddItem(new GUIContent("Toggle Maximize"), w.maximized, () => EditorApplication.delayCall += () => w.maximized = !w.maximized);
            menu.AddItem(new GUIContent("Gather Here (all floating)"), false, () => EditorApplication.delayCall += () =>
            {
                foreach (var f in Docking.Snapshot().Where(c => !c.main).SelectMany(c => c.panes).SelectMany(p => p.tabs).ToList())
                    if (f != w) Docking.Report(Docking.Dock(f, Docking.DockOf(w), DockEdge.Tab));
            });
            menu.AddSeparator("");
            menu.AddItem(new GUIContent("Close"), false, () => EditorApplication.delayCall += w.Close);
            menu.ShowAsContext();
        }

        // Center 40% is a tab drop; otherwise the nearest edge.
        static DockEdge ZoneAt(Rect r, Vector2 m)
        {
            var n = Rect.PointToNormalized(r, m);
            if (n.x > 0.3f && n.x < 0.7f && n.y > 0.3f && n.y < 0.7f) return DockEdge.Tab;
            return NearestEdge(r, m);
        }

        static DockEdge NearestEdge(Rect r, Vector2 m)
        {
            var n = Rect.PointToNormalized(r, m);
            float l = n.x, ri = 1 - n.x, t = n.y, b = 1 - n.y, min = Mathf.Min(l, ri, t, b);
            return min == l ? DockEdge.Left : min == ri ? DockEdge.Right : min == t ? DockEdge.Top : DockEdge.Bottom;
        }

        static void Outline(Rect r, Color c, float t)
        {
            EditorGUI.DrawRect(new Rect(r.x, r.y, r.width, t), c);
            EditorGUI.DrawRect(new Rect(r.x, r.yMax - t, r.width, t), c);
            EditorGUI.DrawRect(new Rect(r.x, r.y, t, r.height), c);
            EditorGUI.DrawRect(new Rect(r.xMax - t, r.y, t, r.height), c);
        }
    }
}
