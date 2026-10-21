using WyvernCenter.Core.Verify;
using WyvernCenter.UI;

namespace WyvernCenter;

/// <summary>
/// "Verify my laptop": a guided check for testers. Readings and key presses first (nothing changed), then, with
/// consent, one setting at a time changed briefly and put back. The answers go into the support report.
/// </summary>
public sealed class VerifyForm : Form
{
    private enum Page { Intro, Step, Consent, Done }

    private readonly AppController app;
    private readonly IReadOnlyList<VerifyStep> steps;
    private readonly List<VerifyResult> results = new();
    private readonly Label lblStep, lblTitle, lblBody, lblLive, lblQuestion;
    private readonly RButton btnPrimary, btnYes, btnNo, btnSkip;
    private readonly System.Windows.Forms.Timer poll = new() { Interval = 600 };
    private readonly List<string> seen = new();
    private Page page = Page.Intro;
    private int index = -1;
    private bool consented, applied, polling, started, ended;
    private string applyNote = "";

    private VerifyStep Current => steps[index];

    public VerifyForm(AppController app)
    {
        this.app = app;
        steps = app.VerifySteps();
        Text = "Verify my laptop";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = Theme.FormBack;
        ForeColor = Theme.ForeMain;
        Font = Theme.UiFont(10f);

        int w = Theme.S(480), pad = Theme.S(18), inner = w - 2 * pad, y = Theme.S(14);
        Label Add(int height, float size, FontStyle style = FontStyle.Regular, Color? color = null)
        {
            var l = new Label
            {
                Bounds = new Rectangle(pad, y, inner, Theme.S(height)),
                Font = Theme.UiFont(size, style),
                ForeColor = color ?? Theme.ForeMain,
                UseMnemonic = false,
            };
            Controls.Add(l);
            y += Theme.S(height) + Theme.S(6);
            return l;
        }
        lblStep = Add(18, 8.5f, color: Theme.ForeDim);
        lblTitle = Add(28, 13f, FontStyle.Bold);
        lblBody = Add(66, 10f);
        lblLive = Add(52, 11.5f, FontStyle.Bold, Theme.Accent);
        lblQuestion = Add(46, 10f, FontStyle.Bold);

        int bh = Theme.S(34), bw = (inner - 3 * Theme.S(8)) / 4;
        RButton Button(int i, string text, bool accent = false) => new()
        {
            Text = text,
            Bounds = new Rectangle(pad + i * (bw + Theme.S(8)), y, bw, bh),
            BorderColor = accent ? Theme.Accent : Theme.ButtonMain,
        };
        btnPrimary = Button(0, "Start", accent: true);
        btnYes = Button(1, "Yes", accent: true);
        btnNo = Button(2, "No");
        btnSkip = Button(3, "Skip");
        Controls.AddRange(new Control[] { btnPrimary, btnYes, btnNo, btnSkip });
        ClientSize = new Size(w, y + bh + Theme.S(16));

        btnPrimary.Click += (_, _) => Primary();
        btnYes.Click += (_, _) => Answer(StepAnswer.Yes);
        btnNo.Click += (_, _) => Answer(StepAnswer.No);
        btnSkip.Click += (_, _) => Answer(StepAnswer.Skipped);
        poll.Tick += (_, _) => Poll();
        HandleCreated += (_, _) => Theme.DarkTitleBar(Handle);
        FormClosed += (_, _) =>
        {
            poll.Dispose();
            if (started && !ended) app.VerifyEnd(results); // puts back anything still changed
        };
        ShowIntro();
    }

    // ---- Pages ----------------------------------------------------------------------------------

    private void ShowIntro()
    {
        page = Page.Intro;
        lblStep.Text = $"{steps.Count} steps";
        lblTitle.Text = "Verify my laptop";
        lblBody.Text = "First Wyvern Center shows what it reads and you press a few keys: nothing is changed. Then, if you agree, " +
                       "it changes a few settings for a few seconds each and always puts them back.";
        lblLive.Text = "";
        lblQuestion.Text = "Your answers go into the support report.";
        SetButtons(primary: "Start");
    }

    private void Next()
    {
        index++;
        seen.Clear();
        applied = false;
        applyNote = "";
        if (index >= steps.Count) { ShowDone(); return; }
        if (Current.Kind == StepKind.Change && !consented) { ShowConsent(); return; }
        page = Page.Step;
        lblStep.Text = $"Step {index + 1} of {steps.Count} · " + Current.Kind switch
        {
            StepKind.Readings => "readings, nothing changes",
            StepKind.Observe => "you press a key, nothing changes",
            _ => "Wyvern changes this briefly, then puts it back",
        };
        lblTitle.Text = Current.Title;
        lblBody.Text = Current.Instruction;
        lblQuestion.Text = Current.Question;
        lblLive.Text = "";
        if (Current.Kind == StepKind.Change)
            SetButtons(primary: "Change it", answers: false, skip: true);
        else
            SetButtons(primary: null, answers: true, skip: true);
        poll.Start();
        Poll();
    }

    private void ShowConsent()
    {
        page = Page.Consent;
        poll.Stop();
        lblStep.Text = "Before the next steps";
        lblTitle.Text = "Short tests";
        lblBody.Text = "The next steps change one setting at a time for a few seconds, then put it back. " +
                       "If you close this window, anything still changed is put back too.";
        lblLive.Text = "";
        lblQuestion.Text = "Run the tests?";
        SetButtons(primary: "Continue", answers: false, skip: true, skipText: "Finish");
    }

    private void ShowDone()
    {
        page = Page.Done;
        poll.Stop();
        if (!ended)
        {
            ended = true;
            app.VerifyEnd(results);
        }
        int yes = results.Count(r => r.Answer == StepAnswer.Yes), no = results.Count(r => r.Answer == StepAnswer.No);
        lblStep.Text = "Finished";
        lblTitle.Text = "Thank you!";
        lblBody.Text = $"{yes} confirmed, {no} not working, {results.Count - yes - no} skipped. Everything Wyvern changed has been put back.";
        lblLive.Text = "";
        lblQuestion.Text = "Create the support report and send it with your answers.";
        SetButtons(primary: "Create report", answers: false, skip: true, skipText: "Close");
    }

    // ---- Buttons ----------------------------------------------------------------------------------

    private void Primary()
    {
        switch (page)
        {
            case Page.Intro:
                btnPrimary.Enabled = false;
                app.VerifyBegin(ok =>
                {
                    if (IsDisposed) return;
                    started = true;
                    if (!ok) consented = false; // readings still work; the test steps will say they can't run
                    Next();
                });
                break;
            case Page.Consent:
                consented = true;
                index--;
                Next();
                break;
            case Page.Step when Current.Kind == StepKind.Change && !applied:
                btnPrimary.Enabled = btnSkip.Enabled = false;
                lblLive.Text = "Changing it…";
                app.VerifyApply(Current, (ok, note) =>
                {
                    if (IsDisposed) return;
                    applied = true;
                    applyNote = note;
                    if (ok)
                    {
                        lblLive.Text = "Changed. " + Trim(note);
                        SetButtons(primary: null, answers: true, skip: true);
                    }
                    else
                    {
                        lblLive.ForeColor = Theme.Warn;
                        lblLive.Text = "Couldn't change it: " + Trim(note);
                        SetButtons(primary: null, answers: false, skip: true, skipText: "Next");
                    }
                });
                break;
            case Page.Done:
                btnPrimary.Enabled = false;
                lblQuestion.Text = "Reading the firmware…";
                app.CreateSupportReport((folder, error) =>
                {
                    if (IsDisposed) return;
                    lblQuestion.ForeColor = error is null ? Theme.ForeMain : Theme.Warn;
                    lblQuestion.Text = error is null ? "Saved and opened. Attach report.md (and dsdt.aml) to your message." : "Couldn't create it: " + error;
                });
                break;
        }
    }

    private void Answer(StepAnswer answer)
    {
        if (page == Page.Consent) { SkipAllChanges(); return; }
        if (page == Page.Done) { Close(); return; }
        poll.Stop();
        var step = Current;
        if (step.Kind == StepKind.Change && applied)
        {
            SetButtons(primary: null, answers: false, skip: false);
            lblLive.Text = "Putting it back…";
            app.VerifyRevert(step, (ok, note) =>
            {
                if (IsDisposed) return;
                Record(step, answer, $"{applyNote}; {note}" + (ok ? "" : " (NOT put back)"));
                lblLive.ForeColor = Theme.Accent;
                Next();
            });
            return;
        }
        Record(step, answer, step.Kind == StepKind.Change ? "not changed" : seen.Count == 0 ? "no reading" : string.Join(" → ", seen));
        Next();
    }

    private void SkipAllChanges()
    {
        for (; index < steps.Count; index++)
            if (steps[index].Kind == StepKind.Change) Record(steps[index], StepAnswer.Skipped, "tests not run");
        ShowDone();
    }

    private void Record(VerifyStep step, StepAnswer answer, string notes) => results.Add(new VerifyResult(step.Id, step.Title, answer, notes));

    // ---- Live values ------------------------------------------------------------------------------

    private void Poll()
    {
        if (polling || page != Page.Step || IsDisposed) return;
        var step = Current;
        if (step.Kind == StepKind.Change && !applied) return;
        polling = true;
        if (step.Setting is null || step.Setting == VerifyPlan.MaxCooling)
            app.VerifySensors(text =>
            {
                polling = false;
                if (IsDisposed || page != Page.Step || Current != step) return;
                if (step.Kind == StepKind.Readings) { lblLive.Text = text; Seen(text.Replace("\n", " / ")); }
                else lblLive.Text = "Changed. " + text.Replace("\n", " · ");
            });
        else
            app.VerifyTrack(step.Setting, (text, changes) =>
            {
                polling = false;
                if (IsDisposed || page != Page.Step || Current != step) return;
                Seen(text);
                if (step.Kind == StepKind.Observe)
                    lblLive.Text = $"Now: {text}" + (changes > 0 ? $"   ·   changed {changes}×" : "   ·   waiting for a change");
                else lblLive.Text = $"Now: {text}";
            });
    }

    private void Seen(string value)
    {
        if (seen.Count == 0 || seen[^1] != value) seen.Add(value);
        if (seen.Count > 12) seen.RemoveAt(1);
    }

    private void SetButtons(string? primary, bool answers = false, bool skip = false, string skipText = "Skip")
    {
        btnPrimary.Visible = btnPrimary.Enabled = primary is not null;
        if (primary is not null) btnPrimary.Text = primary;
        btnYes.Visible = btnNo.Visible = btnYes.Enabled = btnNo.Enabled = answers;
        btnSkip.Visible = btnSkip.Enabled = skip;
        btnSkip.Text = skipText;
    }

    private static string Trim(string note) => note.Length > 120 ? note[..117] + "…" : note;
}
