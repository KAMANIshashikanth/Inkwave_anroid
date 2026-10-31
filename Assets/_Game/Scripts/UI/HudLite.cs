// M1 heads-up display (the full src/ui/hud.js port is M6): match timer, squads, ink tank, special gauge, crosshair
// with the on-target / in-range states, kill feed, banners, the splatted card and the judge's result with a replay;
// the status badges (tracked / poisoned + the poison tint) and the TRACKED tags over enemies your team has tracked.
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace Inkwave
{
    public static partial class G
    {
        public static HudLite hud;
    }

    public class HudLite : MonoBehaviour
    {
        Canvas canvas;
        Text timer, bannerText, splatted, fpsText, resultText, squads;
        Image inkFill, specialFill, hpFill, crossRing, crossDot;
        Image kitFill, kitLaunch;   // kit weapon meter under the crosshair (Canopy Brolly: canopy hp / regrowth, launch charge)
        RectTransform feedRoot, resultPanel, markRoot;
        Text statusText; Image poisonVig;
        readonly List<Text> trackMarks = new List<Text>();
        float bannerT;
        readonly List<(Text text, float t)> feedItems = new List<(Text, float)>();
        public bool menuOpen;

        public static HudLite create()
        {
            var go = new GameObject("HUD");
            var h = go.AddComponent<HudLite>();
            h.build();
            G.hud = h;
            return h;
        }

        Text label(Transform parent, string name, int size, TextAnchor anchor, Vector2 aMin, Vector2 aMax, Vector2 pos, Vector2 sizeDelta)
        {
            var rt = UiSprites.node(name, parent);
            rt.anchorMin = aMin; rt.anchorMax = aMax; rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = sizeDelta; rt.anchoredPosition = pos;
            var t = rt.gameObject.AddComponent<Text>();
            t.font = UiSprites.font; t.fontSize = size; t.fontStyle = FontStyle.Bold; t.alignment = anchor; t.color = Color.white;
            t.raycastTarget = false; t.horizontalOverflow = HorizontalWrapMode.Overflow; t.verticalOverflow = VerticalWrapMode.Overflow;
            var sh = rt.gameObject.AddComponent<Shadow>(); sh.effectDistance = new Vector2(1.5f, -1.5f); sh.effectColor = new Color(0, 0, 0, 0.55f);
            return t;
        }

        Image bar(Transform parent, string name, Vector2 anchor, Vector2 pos, Vector2 size, Color fill)
        {
            var rt = UiSprites.node(name, parent);
            rt.anchorMin = rt.anchorMax = anchor; rt.pivot = new Vector2(0, 0.5f);
            rt.sizeDelta = size; rt.anchoredPosition = pos;
            var bg = rt.gameObject.AddComponent<Image>(); bg.color = new Color(0.08f, 0.07f, 0.11f, 0.55f); bg.raycastTarget = false;
            var f = UiSprites.node("fill", rt);
            f.offsetMin = new Vector2(3, 3); f.offsetMax = new Vector2(-3, -3);
            var img = f.gameObject.AddComponent<Image>(); img.color = fill; img.raycastTarget = false;
            img.type = Image.Type.Filled; img.fillMethod = Image.FillMethod.Horizontal;
            img.sprite = Sprite.Create(Texture2D.whiteTexture, new Rect(0, 0, 4, 4), new Vector2(0.5f, 0.5f));
            return img;
        }

        void build()
        {
            canvas = gameObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 10;   // under the touch layer (15)
            var scaler = gameObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1280, 720);
            scaler.matchWidthOrHeight = 1;
            gameObject.AddComponent<GraphicRaycaster>();
            var root = transform;
            timer = label(root, "timer", 34, TextAnchor.MiddleCenter, new Vector2(0.5f, 1), new Vector2(0.5f, 1), new Vector2(0, -34), new Vector2(200, 50));
            squads = label(root, "squads", 20, TextAnchor.MiddleCenter, new Vector2(0.5f, 1), new Vector2(0.5f, 1), new Vector2(0, -70), new Vector2(600, 30));
            bannerText = label(root, "banner", 72, TextAnchor.MiddleCenter, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0, 120), new Vector2(900, 120));
            splatted = label(root, "splatted", 34, TextAnchor.MiddleCenter, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0, -40), new Vector2(900, 120));
            fpsText = label(root, "fps", 16, TextAnchor.UpperLeft, new Vector2(0, 1), new Vector2(0, 1), new Vector2(70, -20), new Vector2(120, 24));
            inkFill = bar(root, "ink", new Vector2(0, 0), new Vector2(24, 70), new Vector2(220, 22), Color.white);
            specialFill = bar(root, "special", new Vector2(0, 0), new Vector2(24, 40), new Vector2(220, 16), new Color(1, 0.85f, 0.2f));
            hpFill = bar(root, "hp", new Vector2(0, 0), new Vector2(24, 100), new Vector2(160, 10), new Color(1, 0.3f, 0.35f));
            // crosshair
            var ch = UiSprites.node("crosshair", root);
            ch.anchorMin = ch.anchorMax = new Vector2(0.5f, 0.5f); ch.sizeDelta = new Vector2(34, 34); ch.anchoredPosition = Vector2.zero;
            crossRing = UiSprites.image(ch, UiSprites.ring, new Color(1, 1, 1, 0.85f));
            var dot = UiSprites.node("dot", ch); dot.anchorMin = new Vector2(0.4f, 0.4f); dot.anchorMax = new Vector2(0.6f, 0.6f);
            crossDot = dot.gameObject.AddComponent<Image>(); crossDot.sprite = UiSprites.disc; crossDot.raycastTarget = false;
            kitFill = bar(root, "kit", new Vector2(0.5f, 0.5f), new Vector2(-40, -34), new Vector2(80, 9), Color.white);
            kitLaunch = bar(root, "kitLaunch", new Vector2(0.5f, 0.5f), new Vector2(-40, -44), new Vector2(80, 6), Color.white);
            // poison tint (under everything else) + status badges over the ink tank
            var pv = UiSprites.node("poison", root); pv.anchorMin = Vector2.zero; pv.anchorMax = Vector2.one; pv.offsetMin = pv.offsetMax = Vector2.zero; pv.SetAsFirstSibling();
            poisonVig = pv.gameObject.AddComponent<Image>(); poisonVig.color = new Color(0.42f, 0.16f, 0.52f, 0.2f); poisonVig.raycastTarget = false; poisonVig.enabled = false;
            statusText = label(root, "status", 20, TextAnchor.MiddleLeft, new Vector2(0, 0), new Vector2(0, 0), new Vector2(134, 128), new Vector2(220, 26));
            statusText.supportRichText = true;
            markRoot = UiSprites.node("marks", root); markRoot.anchorMin = Vector2.zero; markRoot.anchorMax = Vector2.zero; markRoot.pivot = Vector2.zero; markRoot.sizeDelta = Vector2.zero;
            feedRoot = UiSprites.node("feed", root);
            feedRoot.anchorMin = feedRoot.anchorMax = new Vector2(1, 1); feedRoot.pivot = new Vector2(1, 1);
            feedRoot.sizeDelta = new Vector2(420, 200); feedRoot.anchoredPosition = new Vector2(-24, -120);
            // results
            resultPanel = UiSprites.node("results", root);
            var bg = resultPanel.gameObject.AddComponent<Image>(); bg.color = new Color(0.05f, 0.06f, 0.13f, 0.82f);
            resultText = label(resultPanel, "resultText", 46, TextAnchor.MiddleCenter, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0, 60), new Vector2(1000, 260));
            var btn = UiSprites.node("again", resultPanel);
            btn.anchorMin = btn.anchorMax = new Vector2(0.5f, 0.5f); btn.sizeDelta = new Vector2(320, 70); btn.anchoredPosition = new Vector2(0, -130);
            var bimg = btn.gameObject.AddComponent<Image>(); bimg.color = new Color(1, 0.54f, 0.08f);
            var b = btn.gameObject.AddComponent<Button>();
            b.onClick.AddListener(() => { resultPanel.gameObject.SetActive(false); GameMain.instance.startMatch(); });
            var bl = label(btn, "label", 30, TextAnchor.MiddleCenter, new Vector2(0, 0), new Vector2(1, 1), Vector2.zero, Vector2.zero);
            bl.text = "PLAY AGAIN"; bl.color = new Color(0.08f, 0.07f, 0.11f);
            resultPanel.gameObject.SetActive(false);
            if (UnityEngine.EventSystems.EventSystem.current == null)
            {
                var es = new GameObject("EventSystem");
                es.AddComponent<UnityEngine.EventSystems.EventSystem>();
                es.AddComponent<UnityEngine.InputSystem.UI.InputSystemUIInputModule>();
            }
        }


        public void banner(string text) { bannerText.text = text; bannerT = 1.6f; }
        public void countdown(int n) => banner(n.ToString());

        public void feed(string text, Color color)
        {
            var t = label(feedRoot, "item", 20, TextAnchor.UpperRight, new Vector2(1, 1), new Vector2(1, 1), new Vector2(-210, -14), new Vector2(420, 26));
            t.text = text; t.color = Color.Lerp(color, Color.white, 0.25f);
            feedItems.Insert(0, (t, 4.5f));
            if (feedItems.Count > 5) { Destroy(feedItems[5].text.gameObject); feedItems.RemoveAt(5); }
        }

        public void showSplatted(string by)
        {
            splatted.text = by != null ? $"Splatted by {by}" : "Splatted!";
            splatted.gameObject.SetActive(true);
        }
        public void hideSplatted() => splatted.gameObject.SetActive(false);

        public void showResults(MatchResult r)
        {
            if (r == null) return;
            var loc = G.match?.local;
            int my = loc?.team ?? 0;
            bool won = r.winner == my;
            string a = $"<color=#{ColorUtility.ToHtmlStringRGB(G.teamColors[0])}>{r.coverage.x * 100:0.0}%</color>";
            string b = $"<color=#{ColorUtility.ToHtmlStringRGB(G.teamColors[1])}>{r.coverage.y * 100:0.0}%</color>";
            resultText.supportRichText = true;
            resultText.text = (won ? "VICTORY!" : "DEFEAT") + $"\n{a}   vs   {b}";
            if (loc != null) resultText.text += $"\n<size=26>{loc.stats.turf:0}p turf · {loc.stats.splats} splats · {loc.stats.deaths} deaths</size>";
            resultPanel.gameObject.SetActive(true);
            G.input?.exitLock();
        }

        // hud.js reticle, Canopy Brolly: the dome = canopy hp, or its regrowth while gone (dim); open, it turns your ink and the
        // arc over it fills toward the launch (off the local runner's kit)
        // Tideline Bow / Brine Cutlass: the draw / charge (the bow's ring 1 and full draw tint it)
        void kitMeter(Actor loc)
        {
            var wr = loc.weaponRunner; string kind = loc.weapon.kind;
            bool vis = loc.alive && (G.rig?.mapK ?? 0) < 0.3f;
            // Shaker Bomb being shaken up (shaker.js pips): the charge toward 3 blasts, brighter per charge, flashing at max
            var sh = Shaker.charging(loc);
            if (sh != null && vis)
            {
                kitFill.transform.parent.gameObject.SetActive(true); kitLaunch.transform.parent.gameObject.SetActive(false);
                kitFill.fillAmount = sh.meter;
                Color tc = G.teamColors[loc.team];
                kitFill.color = sh.level >= 3 ? Color.Lerp(tc, Color.white, Mathf.PingPong(Time.time * 4, 0.6f)) : sh.level == 2 ? Color.Lerp(tc, Color.white, 0.35f) : Color.white;
                return;
            }
            if ((kind == "bow" || kind == "blade") && vis)
            {
                float c = wr.charge;
                bool on = c > 0.01f;
                kitFill.transform.parent.gameObject.SetActive(on); kitLaunch.transform.parent.gameObject.SetActive(false);
                if (!on) return;
                kitFill.fillAmount = c;
                bool ring = kind == "bow" && c >= Config.WEAPONS["bow"].ring1;
                kitFill.color = c >= 0.999f ? Color.Lerp(G.teamColors[loc.team], Color.white, Mathf.PingPong(Time.time * 4, 0.5f)) : ring ? Color.Lerp(G.teamColors[loc.team], Color.white, 0.4f) : Color.white;
                return;
            }
            var K = wr.kit as BrollyState;
            bool show = K != null && kind == "brolly" && vis;
            kitFill.transform.parent.gameObject.SetActive(show); kitLaunch.transform.parent.gameObject.SetActive(show && K.state == "ready" && K.open > 0.5f);
            if (!show) return;
            bool up = K.state == "ready", open = up && K.open > 0.5f; float v = up ? K.hp / K.hpMax : K.regrowK;
            kitFill.fillAmount = v;
            kitFill.color = !up ? new Color(1, 1, 1, 0.4f) : open ? Color.Lerp(G.teamColors[loc.team], Color.white, 0.35f) : v < 0.34f ? new Color(1, 0.35f, 0.29f) : Color.white;
            kitLaunch.fillAmount = open ? K.launchK : 0;
            kitLaunch.color = G.teamColors[loc.team];
        }

        // hud.js status badges: TRACKED (an enemy echo orb / lurk mine marked you) and POISONED (murk mist) + its tint
        void statusBadges(Actor loc, Match m)
        {
            bool tr = loc.alive && loc.status.track > 0, po = loc.alive && loc.status.poison > 0;
            string hx(Color c) => ColorUtility.ToHtmlStringRGB(c);
            statusText.text = (tr ? $"<color=#{hx(G.teamColors[1 - loc.team])}>TRACKED</color>  " : "") + (po ? "<color=#c58aff>POISONED</color>" : "");
            if (poisonVig.enabled != po) poisonVig.enabled = po;
            // enemies tracked for your team: a tag over their head (clamped to the screen edge when off screen)
            var cam = Camera.main; int n = 0;
            var cs = canvas.GetComponent<RectTransform>(); float sx = cs.rect.width / Mathf.Max(1, Screen.width), sy = cs.rect.height / Mathf.Max(1, Screen.height);
            if (cam != null && (G.rig?.mapK ?? 0) < 0.3f)
                foreach (var o in m.actors)
                {
                    if (o.team == loc.team || !o.alive || !(o.status.track > 0 && o.status.trackTeam == loc.team)) continue;
                    Vector3 sp = cam.WorldToScreenPoint(ThreeSpace.toUnity(o.pos + Vector3.up * (o.form == "squid" ? 1.0f : 1.9f)));
                    bool behind = sp.z < 0; if (behind) { sp.x = Screen.width - sp.x; sp.y = Screen.height - sp.y; }
                    float x = Mathf.Clamp(sp.x, 40, Screen.width - 40), y = Mathf.Clamp(behind ? 40 : sp.y, 40, Screen.height - 40);
                    if (n >= trackMarks.Count) trackMarks.Add(label(markRoot, "track", 18, TextAnchor.MiddleCenter, Vector2.zero, Vector2.zero, Vector2.zero, new Vector2(240, 24)));
                    var t = trackMarks[n++];
                    t.text = $"▼ {o.name} · TRACKED"; t.color = Color.Lerp(G.teamColors[o.team], Color.white, 0.2f);
                    t.rectTransform.anchoredPosition = new Vector2(x * sx, y * sy);
                    if (!t.gameObject.activeSelf) t.gameObject.SetActive(true);
                }
            for (int i = n; i < trackMarks.Count; i++) if (trackMarks[i].gameObject.activeSelf) trackMarks[i].gameObject.SetActive(false);
        }

        public void update(float dt, Match m, float fps)
        {
            fpsText.text = G.settings.showFps || GameMain.autopilot || Debug.isDebugBuild ? $"{fps:0} fps" : "";
            if (m == null) return;
            float t = Mathf.Max(0, m.time);
            timer.text = $"{Mathf.FloorToInt(t / 60)}:{Mathf.FloorToInt(t % 60):00}";
            // squads: one marker per player, hollow while splatted
            string sq(int team)
            {
                var s = new System.Text.StringBuilder();
                foreach (var a in m.actors) if (a.team == team) s.Append(a.alive ? "●" : "○");
                return $"<color=#{ColorUtility.ToHtmlStringRGB(G.teamColors[team])}>{s}</color>";
            }
            squads.supportRichText = true;
            squads.text = $"{sq(0)}     {sq(1)}";
            var loc = m.local;
            if (loc != null)
            {
                inkFill.fillAmount = loc.ink / PLAYER.inkMax;
                inkFill.color = loc.ink < 18 ? Color.Lerp(G.teamColors[loc.team], Color.white, Mathf.PingPong(Time.time * 4, 1)) : G.teamColors[loc.team];
                specialFill.fillAmount = loc.specialActive != null && G.specials != null ? G.specials.remaining(loc) : loc.specialFrac();   // a running special drains it
                specialFill.color = loc.specialReady() ? Color.Lerp(new Color(1, 0.85f, 0.2f), Color.white, Mathf.PingPong(Time.time * 3, 1)) : new Color(1, 0.85f, 0.2f);
                hpFill.fillAmount = loc.hp / PLAYER.hp;
                var c = m.controller;
                bool on = c != null && c.onTarget != null;
                crossRing.color = on ? G.teamColors[loc.team] : c != null && c.inRange ? new Color(1, 1, 1, 0.9f) : new Color(1, 1, 1, 0.45f);
                crossDot.color = crossRing.color;
                crossRing.transform.parent.gameObject.SetActive(loc.alive && m.state == "playing" && (G.rig?.mapK ?? 0) < 0.3f);
                kitMeter(loc);
                statusBadges(loc, m);
                if (!loc.alive && splatted.gameObject.activeSelf) splatted.text = splatted.text.Split('\n')[0] + $"\n<size=24>Respawn in {Mathf.CeilToInt(loc.respawnTimer)}</size>";
                splatted.supportRichText = true;
            }
            bannerT -= dt;
            bannerText.color = new Color(1, 1, 1, Mathf.Clamp01(bannerT * 2));
            for (int i = feedItems.Count - 1; i >= 0; i--)
            {
                var (text, life) = feedItems[i];
                life -= dt;
                if (life <= 0) { Destroy(text.gameObject); feedItems.RemoveAt(i); continue; }
                feedItems[i] = (text, life);
                text.rectTransform.anchoredPosition = new Vector2(-210, -14 - i * 28);
                var col = text.color; col.a = Mathf.Clamp01(life); text.color = col;
            }
        }
    }
}
