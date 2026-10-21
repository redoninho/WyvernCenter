using WyvernCenter.Core.Verify;
using WyvernCenter.UI;

namespace WyvernCenter;

/// <summary>
/// "Verify my laptop": a guided check for testers. Readings and key presses first (nothing changed), then, with
/// consent, one setting at a time changed briefly and put back; the MSI level is measured under CPU load instead of
/// asked about. The answers go into the support report, which the tester emails to <see cref="VerifyPlan.ReportEmail"/>.
/// </summary>
public sealed class VerifyForm : Form
{
    private enum Page { Intro, Step, Consent, Done }

    private readonly AppController app;
    private readonly IReadOnlyList<VerifyStep> steps;
    private readonly List<VerifyResult> results = new();
    private readonly Label lblStep, lblTitle, lblBody, lblLive, lblQuestion;
    private readonly RButton btnPrimary, btnYes, btnNo, btnSkip, btnCopy;
    private readonly System.Windows.Forms.Timer poll = new() { Interval = 600 };
    private readonly List<string> seen = new();
    private Page page = Page.Intro;
    private int index = -1;
    private bool consented, applied, polling, started, ended;
    private string applyNote = "";
    // The measure step: the run in progress, the level names, and whether its result is in.
    private ModeMeasurement? measure;
    private string levelNow = "your level", levelTest = "the other level";
    private bool measured, viewing;
    // Fan tests (Max Cooling, Cooler Boost): the fan readings from just before the change, for the report.
    private string fansBefore = "";

    private static bool FanStep(VerifyStep s) => s.Setting is VerifyPlan.MaxCooling or "coolerBoost";

    // The fan-curve check (WMI v2): the run in progress and what the writes reported.
    private FanCurveCheck? fanCheck;
    private string fanNotes = "";
    private bool finishingFans;

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
        lblBody = Add(112, 10f);
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
        btnCopy = Button(1, "Copy email");
        btnCopy.Visible = false;
        Controls.AddRange(new Control[] { btnPrimary, btnYes, btnNo, btnSkip, btnCopy });
        ClientSize = new Size(w, y + bh + Theme.S(16));

        btnPrimary.Click += (_, _) => Primary();
        btnYes.Click += (_, _) => Answer(StepAnswer.Yes);
        btnNo.Click += (_, _) => Answer(StepAnswer.No);
        btnSkip.Click += (_, _) => Answer(StepAnswer.Skipped);
        btnCopy.Click += (_, _) =>
        {
            try { Clipboard.SetText(VerifyPlan.ReportEmail); btnCopy.Text = "Copied"; }
            catch { btnCopy.Text = "Couldn't copy"; }
        };
        poll.Tick += (_, _) => Poll();
        HandleCreated += (_, _) => Theme.DarkTitleBar(Handle);
        FormClosed += (_, _) =>
        {
            poll.Dispose();
            StopLoad();
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
        measure = null;
        measured = false;
        fansBefore = "";
        fanCheck = null;
        fanNotes = "";
        finishingFans = false;
        lblLive.ForeColor = Theme.Accent;
        if (index >= steps.Count) { ShowDone(); return; }
        if (Current.Kind is StepKind.Change or StepKind.Measure && !consented) { ShowConsent(); return; }
        page = Page.Step;
        lblStep.Text = $"Step {index + 1} of {steps.Count} · " + Current.Kind switch
        {
            StepKind.Readings => "readings, nothing changes",
            StepKind.Observe => "you press a key, nothing changes",
            StepKind.Measure => "Wyvern measures this under load, then puts it back",
            _ => "Wyvern changes this briefly, then puts it back",
        };
        lblTitle.Text = Current.Title;
        lblBody.Text = Current.Instruction;
        lblQuestion.Text = Current.Question;
        lblLive.Text = "";
        if (Current.Kind == StepKind.Change)
            SetButtons(primary: "Change it", answers: false, skip: true);
        else if (Current.Kind == StepKind.Measure && Current.Setting == VerifyPlan.FanCurve)
            SetButtons(primary: "Run it", answers: false, skip: true);
        else if (Current.Kind == StepKind.Measure)
        {
            SetButtons(primary: "Run it", answers: false, skip: true);
            var step = Current;
            app.VerifyLevelNames(step, (now, test) =>
            {
                if (IsDisposed || page != Page.Step || Current != step) return;
                levelNow = now;
                levelTest = test;
                lblLive.Text = $"Now: {now}. Test: {test}.";
            });
        }
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
        lblQuestion.Text = $"Create the support report, then email report.md and dsdt.aml to {VerifyPlan.ReportEmail}.";
        SetButtons(primary: "Create report", answers: false, skip: true, skipText: "Close", copy: true);
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
            case Page.Step when Current.Kind == StepKind.Measure && measured:
                Next();
                break;
            case Page.Step when Current.Kind == StepKind.Measure && measure is null && fanCheck is null:
                if (Current.Setting == VerifyPlan.FanCurve) StartFanCheck();
                else StartMeasure();
                break;
            case Page.Step when Current.Kind == StepKind.Change && !applied:
                btnPrimary.Enabled = btnSkip.Enabled = false;
                lblLive.Text = "Changing it…";
                if (FanStep(Current)) app.VerifySensors(text => fansBefore = text.Replace("\n", " / ")); // queued before the change
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
                    lblQuestion.Text = error is null
                        ? $"Saved, and its folder is open. Email report.md and dsdt.aml to {VerifyPlan.ReportEmail}."
                        : "Couldn't create it: " + error;
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
        if (step.Kind == StepKind.Measure)
        {
            if (measure is not null) FinishMeasure("stopped by you");
            else if (fanCheck is not null) FinishFanCheck("stopped by you");
            else { Record(step, StepAnswer.Skipped, "not run"); Next(); }
            return;
        }
        if (step.Kind == StepKind.Change && applied)
        {
            SetButtons(primary: null, answers: false, skip: false);
            lblLive.Text = "Putting it back…";
            app.VerifyRevert(step, (ok, note) =>
            {
                if (IsDisposed) return;
                var fans = FanStep(step) ? $"fans before: {(fansBefore == "" ? "no reading" : fansBefore)}, during: {(seen.Count == 0 ? "no reading" : seen[^1])}; " : "";
                Record(step, answer, $"{applyNote}; {fans}{note}" + (ok ? "" : " (NOT put back)"));
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
            if (steps[index].Kind is StepKind.Change or StepKind.Measure) Record(steps[index], StepAnswer.Skipped, "tests not run");
        ShowDone();
    }

    private void Record(VerifyStep step, StepAnswer answer, string notes) => results.Add(new VerifyResult(step.Id, step.Title, answer, notes));

    // ---- Measure step ---------------------------------------------------------------------------
    // Full CPU load in the current level, then in the test level (ModeMeasurement decides when). Samples come from the
    // app's monitor while this window is a viewer. Every way out stops the load and puts the level back.

    private void StartMeasure()
    {
        measure = new ModeMeasurement(DateTime.Now);
        SetButtons(primary: null, answers: false, skip: true, skipText: "Stop");
        lblQuestion.Text = "Running. The fans may get louder.";
        app.SampleArrived += OnSample;
        app.AddViewer();
        viewing = true;
        app.VerifyLoad(true);
        lblLive.Text = measure.Progress(DateTime.Now, null, levelNow);
    }

    private void OnSample()
    {
        if (IsDisposed || page != Page.Step || measure is not { Phase: < 3 } m || app.LastSample is not { } s) return;
        var now = DateTime.Now;
        switch (m.Sample(s, now, app.FailsafeEngaged))
        {
            case MeasureEvent.SwitchNow:
                lblLive.Text = $"Switching to {levelTest}…";
                btnSkip.Enabled = false;
                var step = Current;
                app.VerifyApply(step, (ok, note) =>
                {
                    if (IsDisposed) return;
                    applied = true;
                    applyNote = note;
                    btnSkip.Enabled = true;
                    if (m.Phase == 3) { app.VerifyRevert(step, (_, _) => { }); return; } // ended while switching
                    if (ok) m.Switched(DateTime.Now);
                    else FinishMeasure("couldn't switch the level: " + Trim(note));
                });
                break;
            case MeasureEvent.Finished:
                FinishMeasure(null);
                break;
            case MeasureEvent.Stopped:
                FinishMeasure(m.StopReason);
                break;
            default:
                lblLive.Text = m.Progress(now, s, m.Phase == 1 ? levelNow : levelTest);
                break;
        }
    }

    private void FinishMeasure(string? stopped)
    {
        if (measure is not { } m || measured) return;
        m.Stop(stopped ?? "finished");
        StopLoad();
        var step = Current;
        var (answer, summary) = stopped is null
            ? ModeMeasurement.Verdict(m.Before, m.After, levelNow, levelTest)
            : (StepAnswer.Skipped, "stopped: " + stopped);
        SetButtons(primary: null);
        lblLive.Text = "Putting your level back…";

        void Show(string revertNote)
        {
            if (IsDisposed) return;
            Record(step, answer, string.Join("; ", new[] { summary, applyNote, revertNote }.Where(n => n != "")));
            measured = true;
            lblLive.ForeColor = answer == StepAnswer.Yes ? Theme.Accent : Theme.Warn;
            lblLive.Text = answer switch
            {
                StepAnswer.Yes => "Works: the level changed the CPU's power.",
                StepAnswer.No => "No clear difference between the levels.",
                _ => "Couldn't measure it" + (stopped is null ? "." : $": {stopped}."),
            };
            lblQuestion.Text = (m.Before is null ? "" : $"{levelNow}: {ModeMeasurement.Show(m.Before)}\n") +
                               (m.After is null ? "" : $"{levelTest}: {ModeMeasurement.Show(m.After)}");
            SetButtons(primary: "Next");
        }
        if (applied) app.VerifyRevert(step, (ok, note) => Show(note + (ok ? "" : " (NOT put back)")));
        else Show("");
    }

    // ---- Fan-curve check -------------------------------------------------------------------------
    // FanCurveCheck decides what to do next from the fan readings (polled here); every write goes through the
    // session, and every way out puts the fans back (fan mode first, then the curves).

    private void StartFanCheck()
    {
        SetButtons(primary: null, answers: false, skip: true, skipText: "Stop");
        lblQuestion.Text = "Running. The fans will get loud.";
        lblLive.Text = "Reading the fan mode…";
        var step = Current;
        app.VerifyRead("fanMode", mode =>
        {
            if (IsDisposed || page != Page.Step || Current != step) return;
            fanCheck = new FanCurveCheck(FanCurveCheck.Candidates(mode ?? 0), DateTime.Now);
            if (mode is int m) fanNotes = $"fan mode was 0x{m:X2}";
            else HandleFan(fanCheck.Stop("couldn't read the fan mode"));
        });
    }

    private void OnFanRpm(int? rpm)
    {
        if (IsDisposed || page != Page.Step || fanCheck is not { Now: not FanCurveCheck.Stage.Done } c) return;
        var now = DateTime.Now;
        var e = app.FailsafeEngaged ? c.Stop("the thermal failsafe engaged") : c.Sample(rpm, now);
        if (c.Now != FanCurveCheck.Stage.Done) lblLive.Text = c.Progress(now);
        HandleFan(e);
    }

    private void HandleFan(FanCheckEvent e)
    {
        if (fanCheck is not { } c) return;
        switch (e)
        {
            case FanCheckEvent.TryCandidate:
                var value = c.CandidateValue;
                lblLive.Text = $"Trying {FanCurveCheck.Name(value)}…";
                btnSkip.Enabled = false;
                // The curves are raised every time (a no-op after the first): a candidate never runs on the old curve.
                app.VerifyRun(s =>
                {
                    var raised = s.RaiseCurves();
                    if (!raised.Ok) return raised;
                    var mode = s.SetFanMode(value);
                    return (mode.Ok, raised.Note + "; " + mode.Note);
                }, (ok, note) =>
                {
                    if (IsDisposed) return;
                    btnSkip.Enabled = true;
                    fanNotes += (fanNotes == "" ? "" : "; ") + note;
                    HandleFan(c.Applied(DateTime.Now, ok));
                });
                break;
            case FanCheckEvent.PutModeBack:
                lblLive.Text = "Putting the fan mode back…";
                app.VerifyRun(s => s.PutFanModeBack(), (ok, note) =>
                {
                    if (IsDisposed) return;
                    if (note != "") fanNotes += "; " + note;
                    if (ok) c.ModeBack(DateTime.Now);
                    else HandleFan(c.Stop("couldn't put the fan mode back"));
                });
                break;
            case FanCheckEvent.Finished:
                FinishFanCheck(null);
                break;
        }
    }

    private void FinishFanCheck(string? stoppedBy)
    {
        if (fanCheck is not { } c || measured || finishingFans) return;
        finishingFans = true;
        if (stoppedBy is not null) c.Stop(stoppedBy);
        SetButtons(primary: null);
        lblLive.Text = "Putting your fans back…";
        var step = Current;
        // Queued after any write still in flight, so it always runs last.
        app.VerifyRun(s => s.PutFansBack(), (ok, note) =>
        {
            if (IsDisposed) return;
            var (answer, summary) = c.Verdict();
            Record(step, answer, string.Join("; ", new[] { summary, fanNotes, ok ? note : note + " (NOT put back)" }.Where(n => n != "")));
            measured = true;
            lblLive.ForeColor = answer == StepAnswer.Yes ? Theme.Accent : Theme.Warn;
            lblLive.Text = answer switch
            {
                StepAnswer.Yes => $"Works: custom curves use {FanCurveCheck.Name(c.Confirmed!.Value)}.",
                StepAnswer.No => "Neither setting sped the fans up.",
                _ => $"Couldn't finish: {c.StopReason}.",
            };
            lblQuestion.Text = string.Join("\n", c.Tried.Select(t =>
                $"{FanCurveCheck.Name(t.Value)}: {(t.PeakRpm is int peak ? $"{t.BeforeRpm:F0} → {peak} RPM" : "couldn't be written")}"))
                + (ok ? "" : "\nFans NOT all put back: " + Trim(note));
            SetButtons(primary: "Next");
        });
    }

    private void StopLoad()
    {
        app.VerifyLoad(false);
        app.SampleArrived -= OnSample;
        if (viewing) { app.RemoveViewer(); viewing = false; }
    }

    // ---- Live values ------------------------------------------------------------------------------

    private void Poll()
    {
        if (polling || page != Page.Step || IsDisposed) return;
        var step = Current;
        if (step.Kind == StepKind.Measure)
        {
            if (fanCheck is not { Now: not FanCurveCheck.Stage.Done }) return;
            polling = true;
            app.VerifyFanRpm(rpm =>
            {
                polling = false;
                OnFanRpm(rpm);
            });
            return;
        }
        if (step.Kind == StepKind.Change && !applied) return;
        polling = true;
        if (step.Setting is null || FanStep(step))
            app.VerifySensors(text =>
            {
                polling = false;
                if (IsDisposed || page != Page.Step || Current != step) return;
                Seen(text.Replace("\n", " / "));
                lblLive.Text = step.Kind == StepKind.Readings ? text : "Changed. " + text.Replace("\n", " · ");
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

    private void SetButtons(string? primary, bool answers = false, bool skip = false, string skipText = "Skip", bool copy = false)
    {
        btnCopy.Visible = copy;
        btnPrimary.Visible = btnPrimary.Enabled = primary is not null;
        if (primary is not null) btnPrimary.Text = primary;
        btnYes.Visible = btnNo.Visible = btnYes.Enabled = btnNo.Enabled = answers;
        btnSkip.Visible = btnSkip.Enabled = skip;
        btnSkip.Text = skipText;
    }

    private static string Trim(string note) => note.Length > 120 ? note[..117] + "…" : note;
}
