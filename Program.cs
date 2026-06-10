using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

// DesktopWhip — průhledný celoobrazovkový overlay s fyzikálně simulovaným bičem.
// Provaz = řetěz bodů (Verlet integrace). Hlavu biče taháš myší.
// Čistě vizuální, nijak nezasahuje do oken ani procesů.
// Build: csc /target:winexe /out:DesktopWhip.exe Program.cs
//   (nebo viz README pro dotnet/csproj variantu)

static class Program
{
    [STAThread]
    static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new WhipForm());
    }
}

// Jeden bod provazu pro Verlet integraci.
class Node
{
    public PointF Pos;
    public PointF Prev;
    public Node(float x, float y) { Pos = new PointF(x, y); Prev = Pos; }
}

// Jiskra při prásknutí.
class Particle
{
    public PointF Pos;
    public PointF Vel;
    public float Life; // 1 -> 0
}

// Rázový kruh.
class Ring
{
    public PointF Pos;
    public float Age;
    public float MaxAge;
    public float Grow;
}

// Bod světelné stopy za špičkou.
class TrailPoint
{
    public PointF Pos;
    public float Speed;
}

class WhipForm : Form
{
    // ---- Win32 pro click-through overlay ----
    const int WS_EX_LAYERED = 0x80000;
    const int WS_EX_TRANSPARENT = 0x20;
    const int WS_EX_TOOLWINDOW = 0x80;
    const int GWL_EXSTYLE = -20;

    [DllImport("user32.dll", SetLastError = true)]
    static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll", SetLastError = true)]
    static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    [DllImport("user32.dll")]
    static extern bool GetCursorPos(out POINT lpPoint);
    [DllImport("user32.dll")]
    static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")]
    static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);
    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);
    [DllImport("user32.dll")]
    static extern bool SetForegroundWindow(IntPtr hWnd);
    const uint GA_ROOT = 2;

    [StructLayout(LayoutKind.Sequential)]
    struct POINT { public int X; public int Y; }

    // ---- Simulace ----
    readonly List<Node> nodes = new List<Node>();
    const int SEGMENTS = 28;          // počet článků provazu
    float segLen = 14f;               // klidová délka článku
    const float GRAVITY = 0.12f;      // mírná gravitace (bič není mokrý provaz)
    const float DAMPING = 0.996f;     // skoro bez tření -> drží energii švihu
    const int SUBSTEPS = 3;           // fyzikální podkroky na frame (stabilita při švihu)
    const int CONSTRAINT_ITERS = 10;  // iterace solveru na podkrok

    PointF handTarget;                // kam míří ruka (kurzor)
    PointF handPrev;                  // pro výpočet rychlosti špičky
    float tipSpeed;                   // rychlost špičky -> tloušťka/jas
    readonly Timer timer;

    // ---- Bičování Clauda ----
    const float WHIP_SPEED_THRESHOLD = 60f;  // jak rychlý musí být šleh
    const double WHIP_COOLDOWN_SEC = 6.0;    // pauza mezi šlehy
    const string WHIP_PROMPT = "/btw pracuj rychleji";
    DateTime lastWhip = DateTime.MinValue;
    int flashFrames;                  // vizuální flash po zásahu

    // ---- Úchop a efekty ----
    const int HAND_NODE = 2;          // uzel držený myší = střed gripu (pommel kouká ven)
    const float CRACK_FX_SPEED = 48f; // od jaké rychlosti špičky sypat jiskry
    readonly List<Particle> particles = new List<Particle>();
    readonly List<Ring> rings = new List<Ring>();
    readonly List<TrailPoint> trail = new List<TrailPoint>();
    readonly Random rng = new Random();
    DateTime lastFx = DateTime.MinValue;

    public WhipForm()
    {
        // Overlay přes všechny obrazovky
        var bounds = SystemInformation.VirtualScreen;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Bounds = bounds;
        TopMost = true;
        ShowInTaskbar = false;
        BackColor = Color.Black;
        TransparencyKey = Color.Black;   // černá = průhledná
        DoubleBuffered = true;

        // Inicializace provazu vodorovně od středu
        float sx = bounds.Width / 2f, sy = bounds.Height / 2f;
        for (int i = 0; i < SEGMENTS; i++)
            nodes.Add(new Node(sx + i * segLen, sy));
        handTarget = new PointF(sx, sy);
        handPrev = handTarget;

        timer = new Timer { Interval = 16 }; // ~60 FPS
        timer.Tick += (s, e) => { Step(); Invalidate(); };
        timer.Start();

        // ESC zavře
        KeyPreview = true;
        KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) Close(); };
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW;
            return cp;
        }
    }

    void Step()
    {
        // Globální pozice kurzoru -> lokální souřadnice overlaye
        POINT p;
        GetCursorPos(out p);
        PointF prevHand = handTarget;
        PointF newHand = new PointF(p.X - Bounds.Left, p.Y - Bounds.Top);

        int count = nodes.Count;

        // Fyzika běží v podkrocích a ruka se mezi nimi interpoluje —
        // rychlý pohyb myši tak "protáhne" energii celým bičem místo skoku.
        for (int s = 0; s < SUBSTEPS; s++)
        {
            float ft = (s + 1) / (float)SUBSTEPS;
            handTarget = new PointF(
                prevHand.X + (newHand.X - prevHand.X) * ft,
                prevHand.Y + (newHand.Y - prevHand.Y) * ft);

            // Verlet integrace
            foreach (var nd in nodes)
            {
                float vx = (nd.Pos.X - nd.Prev.X) * DAMPING;
                float vy = (nd.Pos.Y - nd.Prev.Y) * DAMPING;
                nd.Prev = nd.Pos;
                nd.Pos = new PointF(nd.Pos.X + vx, nd.Pos.Y + vy + GRAVITY / SUBSTEPS);
            }

            for (int k = 0; k < CONSTRAINT_ITERS; k++)
            {
                nodes[HAND_NODE].Pos = handTarget; // myš drží grip uprostřed

                // Délkové constrainty (článek = segLen)
                for (int i = 0; i < count - 1; i++)
                {
                    var a = nodes[i];
                    var b = nodes[i + 1];
                    float dx = b.Pos.X - a.Pos.X;
                    float dy = b.Pos.Y - a.Pos.Y;
                    float dist = (float)Math.Sqrt(dx * dx + dy * dy);
                    if (dist < 0.0001f) dist = 0.0001f;
                    float diff = (segLen - dist) / dist;
                    float ox = dx * 0.5f * diff;
                    float oy = dy * 0.5f * diff;
                    bool aFix = (i == HAND_NODE);
                    bool bFix = (i + 1 == HAND_NODE);
                    if (aFix && bFix) continue;
                    if (aFix)
                    {
                        b.Pos = new PointF(b.Pos.X + ox * 2, b.Pos.Y + oy * 2);
                    }
                    else if (bFix)
                    {
                        a.Pos = new PointF(a.Pos.X - ox * 2, a.Pos.Y - oy * 2);
                    }
                    else
                    {
                        a.Pos = new PointF(a.Pos.X - ox, a.Pos.Y - oy);
                        b.Pos = new PointF(b.Pos.X + ox, b.Pos.Y + oy);
                    }
                }

                // Ohybová tuhost: drž body i a i+2 od sebe.
                // Tuhé u rukojeti (skoro rovné), volné ke špičce — jako skutečný bič.
                for (int i = 0; i < count - 2; i++)
                {
                    float t = i / (float)(count - 2);
                    float stiff = 0.55f * (1f - t) * (1f - t); // kvadraticky slábne
                    if (stiff < 0.01f) continue;

                    var a = nodes[i];
                    var c = nodes[i + 2];
                    float dx = c.Pos.X - a.Pos.X;
                    float dy = c.Pos.Y - a.Pos.Y;
                    float dist = (float)Math.Sqrt(dx * dx + dy * dy);
                    if (dist < 0.0001f) dist = 0.0001f;
                    float target = segLen * 2f;
                    if (dist >= target) continue; // narovnávej jen při ohnutí
                    float diff = (target - dist) / dist * stiff;
                    float ox = dx * 0.5f * diff;
                    float oy = dy * 0.5f * diff;
                    bool aFix2 = (i == HAND_NODE);
                    bool cFix2 = (i + 2 == HAND_NODE);
                    if (aFix2 && cFix2) continue;
                    if (aFix2)
                    {
                        c.Pos = new PointF(c.Pos.X + ox * 2, c.Pos.Y + oy * 2);
                    }
                    else if (cFix2)
                    {
                        a.Pos = new PointF(a.Pos.X - ox * 2, a.Pos.Y - oy * 2);
                    }
                    else
                    {
                        a.Pos = new PointF(a.Pos.X - ox, a.Pos.Y - oy);
                        c.Pos = new PointF(c.Pos.X + ox, c.Pos.Y + oy);
                    }
                }
            }
        }
        handTarget = newHand;

        // Rychlost špičky pro efekt švihu
        var tip = nodes[nodes.Count - 1].Pos;
        float tdx = tip.X - handPrev.X, tdy = tip.Y - handPrev.Y;
        tipSpeed = (float)Math.Sqrt(tdx * tdx + tdy * tdy);
        handPrev = tip;

        // ---- Efekty ----
        // Stopa za špičkou
        var tp2 = new TrailPoint();
        tp2.Pos = tip;
        tp2.Speed = tipSpeed;
        trail.Add(tp2);
        if (trail.Count > 16) trail.RemoveAt(0);

        // Jiskry + malý rázový kruh při prásknutí
        if (tipSpeed > CRACK_FX_SPEED &&
            (DateTime.Now - lastFx).TotalMilliseconds > 110)
        {
            lastFx = DateTime.Now;
            float ndx = tdx / tipSpeed, ndy = tdy / tipSpeed;
            int cnt = 8 + rng.Next(7);
            for (int i = 0; i < cnt; i++)
            {
                double ang = (rng.NextDouble() - 0.5) * 1.6; // rozptyl ±~45°
                float ca = (float)Math.Cos(ang), sa = (float)Math.Sin(ang);
                float rx = ndx * ca - ndy * sa;
                float ry = ndx * sa + ndy * ca;
                float spd = 3f + (float)rng.NextDouble() * tipSpeed * 0.18f;
                var pt = new Particle();
                pt.Pos = tip;
                pt.Vel = new PointF(rx * spd, ry * spd);
                pt.Life = 1f;
                particles.Add(pt);
            }
            var r0 = new Ring();
            r0.Pos = tip; r0.Age = 0; r0.MaxAge = 14; r0.Grow = 3.2f;
            rings.Add(r0);
        }

        // Update jisker
        for (int i = particles.Count - 1; i >= 0; i--)
        {
            var pt = particles[i];
            pt.Pos = new PointF(pt.Pos.X + pt.Vel.X, pt.Pos.Y + pt.Vel.Y);
            pt.Vel = new PointF(pt.Vel.X * 0.92f, pt.Vel.Y * 0.92f + 0.18f);
            pt.Life -= 0.055f;
            if (pt.Life <= 0) particles.RemoveAt(i);
        }

        // Update kruhů
        for (int i = rings.Count - 1; i >= 0; i--)
        {
            rings[i].Age += 1f;
            if (rings[i].Age > rings[i].MaxAge) rings.RemoveAt(i);
        }

        // Šleh do okna Clauda -> pošli prompt
        if (tipSpeed > WHIP_SPEED_THRESHOLD &&
            (DateTime.Now - lastWhip).TotalSeconds > WHIP_COOLDOWN_SEC)
        {
            POINT tp;
            tp.X = (int)(tip.X + Bounds.Left);
            tp.Y = (int)(tip.Y + Bounds.Top);
            IntPtr hwnd = WindowFromPoint(tp); // overlay je WS_EX_TRANSPARENT, hit-test ho přeskočí
            if (hwnd != IntPtr.Zero)
            {
                IntPtr root = GetAncestor(hwnd, GA_ROOT);
                var sb = new StringBuilder(256);
                GetWindowText(root, sb, sb.Capacity);
                string title = sb.ToString();
                if (title.IndexOf("claude", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    lastWhip = DateTime.Now;
                    flashFrames = 12;
                    var rr = new Ring();
                    rr.Pos = new PointF(tip.X, tip.Y);
                    rr.Age = 0; rr.MaxAge = 26; rr.Grow = 6f;
                    rings.Add(rr);
                    SetForegroundWindow(root);
                    System.Threading.Thread.Sleep(200); // dej oknu čas převzít fokus
                    try
                    {
                        // Záloha schránky uživatele
                        string oldClip = null;
                        try { if (Clipboard.ContainsText()) oldClip = Clipboard.GetText(); }
                        catch (Exception) { }

                        Clipboard.SetText(WHIP_PROMPT);

                        SendKeys.SendWait("^a"); // označ případný zbylý text
                        System.Threading.Thread.Sleep(60);
                        SendKeys.SendWait("^v"); // atomické vložení celého textu
                        System.Threading.Thread.Sleep(180);
                        SendKeys.SendWait("{ENTER}");
                        System.Threading.Thread.Sleep(100);
                        SendKeys.SendWait("{ENTER}"); // pojistka; na prázdném inputu nic neudělá

                        // Vrať schránku do původního stavu
                        try
                        {
                            if (oldClip != null) Clipboard.SetText(oldClip);
                            else Clipboard.Clear();
                        }
                        catch (Exception) { }
                    }
                    catch (Exception) { /* okno mezitím zmizelo apod. */ }
                }
            }
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        // ---- Světelná stopa za špičkou (pod bičem) ----
        for (int i = 1; i < trail.Count; i++)
        {
            float ft = i / (float)trail.Count;
            float spdA = Math.Min(255f, trail[i].Speed * 4f);
            int alpha = (int)(spdA * ft * 0.8f);
            if (alpha < 8) continue;
            float w = 1f + 5f * ft * Math.Min(1f, trail[i].Speed / 60f);
            using (var tpen = new Pen(Color.FromArgb(alpha, 255, 235, 160), w))
            {
                tpen.StartCap = LineCap.Round;
                tpen.EndCap = LineCap.Round;
                g.DrawLine(tpen, trail[i - 1].Pos, trail[i].Pos);
            }
        }

        // ---- Pletený kožený řemen (thong) ----
        // Tenčí profil, tmavá kůže, lesk po horní hraně, příčné "pletení".
        int n = nodes.Count;
        for (int i = 0; i < n - 1; i++)
        {
            float t = i / (float)(n - 1);
            float width = 5.5f * (1f - t) + 0.8f;

            int glow = Math.Min(90, (int)(tipSpeed * 1.2f * t));

            // základ: tmavě hnědá kůže
            var baseCol = Color.FromArgb(255,
                Clamp(56 + glow), Clamp(36 + glow / 2), Clamp(22 + glow / 3));
            using (var pen = new Pen(baseCol, width))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                g.DrawLine(pen, nodes[i].Pos, nodes[i + 1].Pos);
            }

            // úzký světlý proužek = lesk na hraně řemenu
            var hiCol = Color.FromArgb(130,
                Clamp(125 + glow), Clamp(88 + glow / 2), 52);
            using (var pen2 = new Pen(hiCol, Math.Max(0.7f, width * 0.3f)))
            {
                pen2.StartCap = LineCap.Round;
                pen2.EndCap = LineCap.Round;
                float off = width * 0.22f;
                g.DrawLine(pen2,
                    nodes[i].Pos.X, nodes[i].Pos.Y - off,
                    nodes[i + 1].Pos.X, nodes[i + 1].Pos.Y - off);
            }

            // pletení: tmavá příčná čárka v každém druhém článku
            if (i % 2 == 0 && width > 1.6f)
            {
                float dx = nodes[i + 1].Pos.X - nodes[i].Pos.X;
                float dy = nodes[i + 1].Pos.Y - nodes[i].Pos.Y;
                float len = (float)Math.Sqrt(dx * dx + dy * dy);
                if (len < 0.001f) len = 0.001f;
                float nx = -dy / len, ny = dx / len;
                float mx = (nodes[i].Pos.X + nodes[i + 1].Pos.X) / 2f;
                float my = (nodes[i].Pos.Y + nodes[i + 1].Pos.Y) / 2f;
                using (var pen3 = new Pen(Color.FromArgb(150, 18, 11, 7), 1f))
                    g.DrawLine(pen3,
                        mx - nx * width * 0.45f, my - ny * width * 0.45f,
                        mx + nx * width * 0.45f, my + ny * width * 0.45f);
            }
        }

        // ---- Rukojeť: pevný omotaný grip přes první 3 články ----
        using (var hp = new Pen(Color.FromArgb(255, 32, 20, 13), 8f))
        {
            hp.StartCap = LineCap.Round;
            hp.EndCap = LineCap.Round;
            for (int i = 0; i < 3 && i < n - 1; i++)
                g.DrawLine(hp, nodes[i].Pos, nodes[i + 1].Pos);
        }
        // omotávka gripu
        for (int i = 0; i < 3 && i < n - 1; i++)
        {
            float dx = nodes[i + 1].Pos.X - nodes[i].Pos.X;
            float dy = nodes[i + 1].Pos.Y - nodes[i].Pos.Y;
            float len = (float)Math.Sqrt(dx * dx + dy * dy);
            if (len < 0.001f) len = 0.001f;
            float nx = -dy / len, ny = dx / len;
            for (int w = 0; w < 2; w++)
            {
                float ft = (w + 1) / 3f;
                float wx = nodes[i].Pos.X + dx * ft;
                float wy = nodes[i].Pos.Y + dy * ft;
                using (var wp = new Pen(Color.FromArgb(180, 70, 45, 28), 1.3f))
                    g.DrawLine(wp, wx - nx * 4f, wy - ny * 4f, wx + nx * 4f, wy + ny * 4f);
            }
        }
        // hlavice (pommel) na konci rukojeti
        var h = nodes[0].Pos;
        using (var hb = new SolidBrush(Color.FromArgb(255, 50, 32, 20)))
            g.FillEllipse(hb, h.X - 5.5f, h.Y - 5.5f, 11, 11);
        using (var hr = new Pen(Color.FromArgb(255, 110, 80, 50), 1.5f))
            g.DrawEllipse(hr, h.X - 5.5f, h.Y - 5.5f, 11, 11);
        // kroužek (keeper) mezi rukojetí a řemenem
        if (n > 3)
        {
            var k = nodes[3].Pos;
            using (var kr = new Pen(Color.FromArgb(255, 140, 120, 80), 2f))
                g.DrawEllipse(kr, k.X - 4, k.Y - 4, 8, 8);
        }

        // ---- Špička: cracker — vějířek tenkých třásní ----
        var tip = nodes[n - 1].Pos;
        var pre = nodes[n - 2].Pos;
        {
            float dx = tip.X - pre.X, dy = tip.Y - pre.Y;
            float len = (float)Math.Sqrt(dx * dx + dy * dy);
            if (len < 0.001f) { dx = 1; dy = 0; len = 1; }
            dx /= len; dy /= len;
            float[] angles = { -0.45f, 0f, 0.45f };
            using (var cp = new Pen(Color.FromArgb(220, 215, 190, 130), 1.1f))
            {
                cp.EndCap = LineCap.Round;
                for (int a2 = 0; a2 < angles.Length; a2++)
                {
                    float ca = (float)Math.Cos(angles[a2]);
                    float sa = (float)Math.Sin(angles[a2]);
                    float rx = dx * ca - dy * sa;
                    float ry = dx * sa + dy * ca;
                    g.DrawLine(cp, tip.X, tip.Y, tip.X + rx * 9f, tip.Y + ry * 9f);
                }
            }
        }

        // Flash po zásahu Clauda
        if (flashFrames > 0)
        {
            flashFrames--;
            int a = 20 * flashFrames;
            if (a > 200) a = 200;
            using (var fp = new Pen(Color.FromArgb(a, 255, 220, 60), 24f))
            {
                fp.StartCap = LineCap.Round;
                fp.EndCap = LineCap.Round;
                var t2 = nodes[nodes.Count - 1].Pos;
                g.DrawEllipse(fp, t2.X - 20, t2.Y - 20, 40, 40);
            }
        }

        // ---- Jiskry ----
        for (int i = 0; i < particles.Count; i++)
        {
            var pt = particles[i];
            int a3 = (int)(255 * pt.Life);
            if (a3 < 0) a3 = 0; if (a3 > 255) a3 = 255;
            var pc = Color.FromArgb(a3, 255, Clamp(200 + (int)(55 * pt.Life)), 120);
            using (var pp = new Pen(pc, 1.4f))
            {
                pp.EndCap = LineCap.Round;
                g.DrawLine(pp, pt.Pos.X, pt.Pos.Y,
                    pt.Pos.X - pt.Vel.X * 1.6f, pt.Pos.Y - pt.Vel.Y * 1.6f);
            }
        }

        // ---- Rázové kruhy ----
        for (int i = 0; i < rings.Count; i++)
        {
            var r = rings[i];
            float prog = r.Age / r.MaxAge;
            int a4 = (int)(210 * (1f - prog));
            if (a4 < 5) continue;
            float rad = 4f + r.Age * r.Grow;
            using (var rp = new Pen(Color.FromArgb(a4, 255, 220, 90), 2.5f * (1f - prog) + 0.5f))
                g.DrawEllipse(rp, r.Pos.X - rad, r.Pos.Y - rad, rad * 2, rad * 2);
        }

        // Nápověda
        using (var f = new Font("Segoe UI", 9))
        using (var sb = new SolidBrush(Color.FromArgb(140, 255, 255, 255)))
            g.DrawString("DesktopWhip — švihni do okna Clauda = \"pracuj rychleji\". ESC = konec.", f, sb, 12, 12);
    }

    static int Clamp(int v)
    {
        return v < 0 ? 0 : (v > 255 ? 255 : v);
    }
}