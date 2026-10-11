using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using OsrsFlipping.Core;

namespace OsrsFlipping.Desktop;

public sealed class MainForm : Form
{
    public EngineWorker Worker { get; }
    public Button StartButton { get; } = new() { Text = "BAŞLAT", AutoSize = true, MinimumSize = new Size(95, 33) };
    public Button StopButton { get; } = new() { Text = "DURDUR", AutoSize = true, MinimumSize = new Size(95, 33) };
    public Button ManualButton { get; } = new() { Text = "ELLE YÖNET", AutoSize = true, MinimumSize = new Size(105, 33) };
    public DataGridView ProfitTable => profitGrid;
    internal DataGridView RecoveryTable => recoveryGrid;
    public string ProfitSummary => profitSummary.Text;
    private readonly string folder;
    private readonly bool startCycle;
    private readonly bool smoke;
    private readonly ComboBox modeBox = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 125 };
    private readonly Label status = new() { Text = "Bağlantı bekleniyor", AutoSize = false, Height = 45, Dock = DockStyle.Top, Padding = new Padding(0, 8, 0, 0) };
    private readonly Panel blockerPanel = new() { Dock = DockStyle.Top, Height = 64, BackColor = Color.LemonChiffon, Visible = false, Padding = new Padding(8) };
    internal Label BlockerMessage { get; } = new() { Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
    internal Button ResolveBlockerButton { get; } = new() { Dock = DockStyle.Right, Width = 210, Text = "Alış geçmişini doğrula" };
    private readonly Label candidateNote = new() { Text = "Bir eşya seçerek kontrol gerekçesini görebilirsin. Uygun aday için de gerçek margin testi gerekir.", Dock = DockStyle.Fill, AutoEllipsis = true };
    private readonly Label profitSummary = new() { Text = "Hesap bağlantısı bekleniyor; gerçekleşmiş kâr henüz yüklenmedi.", AutoSize = false, Dock = DockStyle.Top, Height = 32, Font = new Font("Segoe UI", 10, FontStyle.Bold) };
    private readonly Label profitDetail = new() { AutoSize = false, Dock = DockStyle.Top, Height = 42 };
    private readonly Label profitNote = new() { Text = "Satış GP, GE vergisi sonrası alınan paradır. Kısmi satış maliyeti satılan adede oranlanır. Kalan stok ve banka para hareketleri kâr sayılmaz.", Dock = DockStyle.Bottom, Height = 40 };
    private readonly DataGridView marketGrid = Grid();
    private readonly DataGridView profitGrid = Grid();
    private readonly DataGridView recoveryGrid = Grid();
    private readonly Label recoveryNote = new() { Dock = DockStyle.Top, Height = 70, Text = "Bekleyen uzlaştırma yok. Elle Collect kullandıysan, doğrulanabilen satış burada görünür." };
    private readonly TextBox offersText = TextArea();
    private readonly TextBox logText = TextArea();
    private readonly System.Windows.Forms.Timer eventsTimer = new() { Interval = 200 };
    private readonly System.Windows.Forms.Timer startTimer = new() { Interval = 10000 };
    private readonly ToolTip toolTips = new();
    private RuneLiteActivityMonitor? activityMonitor;
    private long lastProcessBinding;
    private JsonObject report = J.O(("candidates", new JsonArray()));
    private bool closing;
    private bool closeReady;
    private bool started;
    private static readonly CultureInfo Numbers = CultureInfo.GetCultureInfo("en-US");

    public MainForm(string folder, string mode = "LIVE", bool startCycle = false, bool smoke = false)
    {
        this.folder = folder; this.startCycle = startCycle; this.smoke = smoke;
        Text = "OSRS Flipping Companion · C#";
        StartPosition = FormStartPosition.CenterScreen; Size = new Size(1280, 800); MinimumSize = new Size(980, 660);
        Font = new Font("Segoe UI", 9); BackColor = SystemColors.Control; AutoScaleMode = AutoScaleMode.Dpi;
        Worker = new EngineWorker(folder, mode);
        var outer = new Panel { Dock = DockStyle.Fill, Padding = new Padding(16) }; Controls.Add(outer);
        var title = new Label { Text = "OSRS Flipping Companion", Font = new Font("Segoe UI", 20, FontStyle.Bold), Dock = DockStyle.Top, Height = 43 };
        var summary = new Label { Text = "Mevcut tüm GP · hesaba göre GE slotları · özel para/zarar sınırı yok · yerel C#/.NET uygulaması", Dock = DockStyle.Top, Height = 31 };
        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 46, WrapContents = false, Padding = new Padding(0, 3, 0, 3) };
        modeBox.Items.AddRange(["SIMULATION", "HISTORY", "LIVE"]); modeBox.SelectedItem = modeBox.Items.Contains(mode) ? mode : "LIVE";
        modeBox.SelectedIndexChanged += (_, _) => Worker.Post("mode", J.O(("mode", modeBox.Text)));
        toolbar.Controls.Add(modeBox); toolbar.Controls.Add(Button("Wiki piyasasını tara", () => Worker.Post("scan"))); toolbar.Controls.Add(Button("Demo verisi", () => Worker.Post("demo"))); toolbar.Controls.Add(Button("Bağlantılı RuneLite aç", LaunchBridge));
        toolbar.Controls.Add(StartButton); toolbar.Controls.Add(StopButton); toolbar.Controls.Add(ManualButton);
        StartButton.Click += (_, _) => { Worker.Post("start"); status.Text = "Başlatma istendi; bağlantı ve mevcut teklifler kontrol ediliyor."; Log("BAŞLAT istendi. Durdurma sürüyorsa tamamlandıktan sonra otomatik başlayacak."); };
        StopButton.Click += (_, _) => { Worker.Post("pause"); status.Text = "Durdurma istendi; yeni alışlar kesildi, doğrulanmış bot teklifleri toplanıyor."; Log("DURDUR istendi: doğrulanmış bot teklifleri iptal edilip toplanacak; elle açılan veya belirsiz teklifler korunacak."); };
        ManualButton.Click += (_, _) => { Worker.Post("manual"); status.Text = "Elle yönetim: otomatik tıklamalar duraklatıldı; teklifler korunuyor. Devam için BAŞLAT."; Log("ELLE YÖNET istendi. Collect, iptal veya banka işlemlerini yapabilirsin; devam etmeden önce kayıtlar uzlaştırılacak."); };
        toolTips.SetToolTip(StopButton, "Yeni alımları durdurur; açık bot tekliflerini iptal ederek para ve eşyaları toplar.");
        toolTips.SetToolTip(ManualButton, "Uzun elle yönetim için kalıcı duraklatır; devam için BAŞLAT. Kısa RuneLite müdahaleleri ayrıca otomatik algılanır.");
        blockerPanel.Controls.Add(BlockerMessage); blockerPanel.Controls.Add(ResolveBlockerButton);
        ResolveBlockerButton.Click += (_, _) => Baseline(true);
        var tabs = new TabControl { Dock = DockStyle.Fill }; var opportunities = Tab("Fırsatlar"); var profits = Tab("Kâr ve işlemler"); var offers = Tab("Teklifler ve test"); var limits = Tab("Limit ve kayıt"); var recovery = Tab("Kurtarma"); var logs = Tab("Günlük");
        tabs.TabPages.AddRange([opportunities, profits, offers, limits, recovery, logs]);
        ConfigureColumns(marketGrid, [("id", "ID", 65), ("name", "Eşya", 230), ("buy", "Alış GP", 95), ("sell", "Satış GP", 95), ("net", "Net GP/adet", 105), ("volume", "Saatlik adet", 110), ("velocity", "Hız tahmini", 110), ("eligibility", "Kontrol", 95)]);
        marketGrid.SelectionChanged += (_, _) => ExplainCandidate();
        var candidatePanel = new TableLayoutPanel { Dock = DockStyle.Bottom, Height = 110, RowCount = 2, ColumnCount = 1, Padding = new Padding(0, 8, 0, 0) };
        candidatePanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); candidatePanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 37)); candidatePanel.Controls.Add(candidateNote, 0, 0); candidatePanel.Controls.Add(Button("Seçili eşyaya margin testi", RunMargin), 0, 1);
        opportunities.Controls.Add(marketGrid); opportunities.Controls.Add(candidatePanel);
        ConfigureColumns(profitGrid, [("time", "Zaman", 125), ("item", "Eşya", 170), ("kind", "İşlem", 95), ("bought", "Alınan", 70), ("sold", "Satılan", 70), ("remaining", "Kalan", 70), ("avg", "Ort. alış GP", 105), ("cost", "Alış maliyeti", 105), ("proceeds", "Net satış GP", 105), ("profit", "Net kâr GP", 95), ("status", "Durum", 280)]);
        profits.Controls.Add(profitGrid); profits.Controls.Add(profitNote); profits.Controls.Add(profitDetail); profits.Controls.Add(profitSummary);
        offers.Controls.Add(offersText); logs.Controls.Add(logText); BuildLimits(limits);
        ConfigureColumns(recoveryGrid, [("item", "Eşya", 220), ("stage", "Beklenen işlem", 150), ("expected", "Kayıtlı adet", 110), ("inventory", "Envanterde", 110), ("reason", "Gerekçe", 520)]);
        var recoveryButtons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 42 };
        recoveryButtons.Controls.Add(Button("Elle topladığım satışı doğrula", ConfirmExternalCollection));
        recovery.Controls.Add(recoveryGrid); recovery.Controls.Add(recoveryButtons); recovery.Controls.Add(recoveryNote);
        outer.Controls.Add(tabs); outer.Controls.Add(blockerPanel); outer.Controls.Add(status); outer.Controls.Add(toolbar); outer.Controls.Add(summary); outer.Controls.Add(title);
        if (mode == "LIVE") tabs.SelectedTab = profits;
        eventsTimer.Tick += (_, _) => { RefreshActivityBinding(); DrainEvents(); };
        startTimer.Tick += (_, _) => { startTimer.Stop(); Worker.Post("start"); };
        Shown += (_, _) =>
        {
            if (started) return; started = true; Worker.Start(); eventsTimer.Start(); Log("Veri ve RuneLite bağlantı klasörü: " + folder);
            if (!smoke)
            {
                activityMonitor = new RuneLiteActivityMonitor(); activityMonitor.PhysicalActivity += Worker.NotifyPhysicalUserActivity;
                RefreshActivityBinding();
                Log("Fiziksel tıklama ve klavye otomatik döngüyü durdurmaz. Yalnızca DURDUR veya ELLE YÖNET otomatik girdileri keser.");
            }
            if (startCycle && !smoke) { Worker.Post("scan"); startTimer.Start(); }
        };
        FormClosing += CloseSafely;
        Shown += (_, _) => BeginInvoke(() => { ShowInTaskbar = true; WindowState = FormWindowState.Normal; Show(); Activate(); });
    }
    private static DataGridView Grid() => new() { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false, AllowUserToResizeRows = false, MultiSelect = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, RowHeadersVisible = false, BackgroundColor = Color.White, BorderStyle = BorderStyle.FixedSingle, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None };
    private static TextBox TextArea() => new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Font = new Font("Consolas", 9) };
    private static TabPage Tab(string text) => new(text) { Padding = new Padding(8), UseVisualStyleBackColor = true };
    private static Button Button(string text, Action clicked)
    {
        var button = new Button { Text = text, AutoSize = true, MinimumSize = new Size(0, 33), Margin = new Padding(5, 0, 0, 0) }; button.Click += (_, _) => clicked(); return button;
    }
    private static void ConfigureColumns(DataGridView grid, (string Name, string Header, int Width)[] columns)
    {
        foreach (var (name, header, width) in columns) grid.Columns.Add(new DataGridViewTextBoxColumn { Name = name, HeaderText = header, Width = width, SortMode = DataGridViewColumnSortMode.NotSortable });
        grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
        grid.EnableHeadersVisualStyles = true;
    }
    private void BuildLimits(TabPage page)
    {
        var box = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(8) };
        box.Controls.Add(new Label { Text = "Alış limitleri gözlenen dolumlardan hesaplanır. İlk bağlantıda önceki 4 saat bilinmez.\r\nDört saat kesintisiz izleme veya doğru bir başlangıç beyanı gerekir.", AutoSize = true, MaximumSize = new Size(1000, 0), Margin = new Padding(0, 0, 0, 18) });
        box.Controls.Add(Button("Son 4 saatte hiç alım yapmadım", () => Baseline(false)));
        box.Controls.Add(Button("Kayıtlı bot alımları dışında alım yapmadım", () => Baseline(true)));
        box.Controls.Add(new Label { Text = "İlk düğme gerçekten son 4 saatte hiç alış yoksa kullanılmalı. İkinci düğme yalnızca botun kayıtlı alışları varken dışarıdan başka alış yapmadıysan kullanılmalı.\r\n\r\nDURDUR açık GE tekliflerini iptal edip para/eşyaları toplar; kalan bot stoğunu bankada korur.\r\nBelirsiz sonuçlar tekrar emir gönderilmeden hesap, teklif ve bakiye ile doğrulanır.\r\nRuneLite arka plan girdileri için köprünün bu özelliği desteklemesi gerekir.", AutoSize = true, MaximumSize = new Size(1000, 0), Margin = new Padding(0, 18, 0, 0) });
        page.Controls.Add(box);
    }
    private void Baseline(bool untracked)
    {
        var question = untracked ? "Son 4 saatte botun kayıtlı alışları dışında başka bir GE alımı yapmadığını doğruluyor musun?" : "Bu hesapta son 4 saatte margin testleri dahil hiçbir GE alımı yapmadığını doğruluyor musun?";
        if (MessageBox.Show(this, question, "Alış geçmişi beyanı", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes) Worker.Post(untracked ? "baseline_untracked" : "baseline");
    }
    private void RunMargin()
    {
        if (marketGrid.SelectedRows.Count > 0 && marketGrid.SelectedRows[0].Tag is JsonObject c) Worker.Post("margin", J.O(("item_id", c.L("id"))));
    }
    private void ConfirmExternalCollection()
    {
        if (recoveryGrid.SelectedRows.Count != 1 || recoveryGrid.SelectedRows[0].Tag is not JsonObject row
            || row["manual_collection_plan"] is not JsonObject plan)
        { MessageBox.Show(this, "Bu işlem için tam bakiye/eşya eşleşmesi henüz yok. Kaydı silmeden teklif ve tahsilat geçmişini kontrol edin.", "Uzlaştırma bekliyor"); return; }
        var message = $"{plan.S("item_name")}: {Number(plan.L("sold_quantity"))} adet için {Number(plan.L("net_proceeds_gp"))} GP net satış parası.\r\n\r\nBu satışı tamamlayıp parasını kendin topladığını doğruluyor musun? İşlem kayıtlı bakiyeyle eşleşiyor; onayın günlüğe kaydedilecek.";
        if (MessageBox.Show(this, message, "Elle tahsilat doğrulaması", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        var confirmation = plan.Copy(); confirmation["confirmed"] = true; confirmation["confirmation_id"] = Guid.NewGuid().ToString();
        Worker.Post("confirm_external_collection", confirmation);
    }
    private void ExplainCandidate()
    {
        if (marketGrid.SelectedRows.Count == 0 || marketGrid.SelectedRows[0].Tag is not JsonObject c) return;
        var labels = new Dictionary<string, string> { ["ONE_SIDED_FLOW"] = "Hacim tek tarafa yığılmış", ["HISTORY_NOT_VERIFIED"] = "Yakın fiyat geçmişi doğrulanmadı", ["HISTORY_NOT_CHECKED"] = "Geçmiş henüz incelenmedi", ["RECENT_DOWNTREND"] = "Yakın fiyatlar düşüyor", ["UNSTABLE_PRICE"] = "Fiyat hareketi fazla", ["UNUSUAL_SPREAD"] = "Marj geçmişe göre olağan dışı", ["FRAGILE_MARGIN"] = "Küçük fiyat düşüşü kârı siliyor", ["TEST_COST_NOT_RECOVERED"] = "Test maliyeti karşılanmıyor", ["STRESS_TEST_COST_NOT_RECOVERED"] = "Fiyat stresinde test maliyeti karşılanmıyor", ["TWO_SIDED_LIQUIDITY"] = "İki tarafta hacim var", ["POSITIVE_STRESSED_MARGIN"] = "Fiyat stresinde marj pozitif", ["TEST_COST_RECOVERABLE"] = "Test maliyeti tahminen karşılanabilir", ["SIMULATION"] = "Yapay demo verisi", ["OBSERVED_POOR_PERFORMANCE"] = "Kayıtlı sonuçlar zayıf" };
        var reasons = string.Join(", ", c.Arr("strategy_reasons").Select(n => labels.GetValueOrDefault(n.S(), n.S())));
        candidateNote.Text = $"{c.S("name")} | Gerekçe: {reasons} | Stres marjı: {c.L("stress_net_margin_gp"):N0} GP/adet | Test sonrası tahmini pozisyon kârı: {c.L("estimated_profit_after_test_gp"):N0} GP. Bu hesaplar gerçekleşmiş kâr veya dolum garantisi değildir.";
    }
    private void RenderMarket(JsonObject next)
    {
        report = next; var selected = marketGrid.SelectedRows.Count > 0 ? (marketGrid.SelectedRows[0].Tag as JsonObject)?.L("id") : null;
        marketGrid.SuspendLayout(); marketGrid.Rows.Clear();
        foreach (var c in report.Objects("candidates").Take(150))
        {
            var index = marketGrid.Rows.Add(c.L("id"), c.S("name"), Number(c.L("indicative_buy_gp")), Number(c.L("indicative_sell_gp")), Number(c.L("estimated_net_margin_gp")), Number(c.L("volume_1h_buy") + c.L("volume_1h_sell")), c.S("velocity"), c.B("strategy_eligible", true) ? "UYGUN" : "BEKLE");
            marketGrid.Rows[index].Tag = c; if (c.L("id") == selected) marketGrid.Rows[index].Selected = true;
        }
        marketGrid.ResumeLayout(); ExplainCandidate(); Log($"{report.S("mode")} tarama: {report.Arr("candidates").Count} aday. Elenmeler: {report.Obj("rejected_counts").ToJsonString()}");
    }
    public void RenderProfit(JsonObject next)
    {
        if (InvokeRequired) { BeginInvoke(() => RenderProfit(next)); return; }
        var totals = next.Obj("totals"); var mode = next.S("mode", modeBox.Text);
        if (next.S("account_key") == "") { profitSummary.Text = mode + " | Hesap bağlantısı bekleniyor"; profitDetail.Text = ""; }
        else
        {
            profitSummary.Text = $"{mode} | İşlem kârı: {Signed(totals.L("position_profit_gp"))} GP | Margin testleri neti: {Signed(totals.L("margin_test_profit_gp"))} GP | Toplam net: {Signed(totals.L("net_profit_gp"))} GP";
            profitDetail.Text = $"Bu hesabın kayıtlı geçmişi | {totals.L("closed_position_count")} tamamlanan, {totals.L("active_position_count")} aktif pozisyon | Alış maliyeti: {Number(totals.L("buy_cost_gp"))} GP | Net satış tahsilatı: {Number(totals.L("net_sale_proceeds_gp"))} GP | Kalan stok maliyeti: {Number(totals.L("remaining_cost_gp"))} GP";
        }
        var selected = profitGrid.SelectedRows.Count > 0 ? profitGrid.SelectedRows[0].Tag as string : null;
        var firstRow = profitGrid.FirstDisplayedScrollingRowIndex;
        profitGrid.SuspendLayout(); profitGrid.Rows.Clear();
        foreach (var row in next.Objects("rows"))
        {
            var stamp = row.L("updated_at", row.L("completed_at", row.L("started_at"))); var acquired = row.L("acquired_quantity"); var cost = row.L("buy_cost_gp"); var profit = row.L("realized_profit_gp");
            var unknown = row.S("kind") == "EXTERNAL_COMPLETION";
            var index = profitGrid.Rows.Add(stamp > 0 ? DateTimeOffset.FromUnixTimeSeconds(stamp).ToLocalTime().ToString("dd.MM HH:mm:ss") : "—", row.S("item_name"), unknown ? "Elle tamamlandı" : row.S("kind") == "MARGIN_TEST" ? "Margin testi" : "Al/sat", unknown ? "—" : Number(acquired), unknown ? "—" : Number(row.L("sold_quantity_observed", row.L("sold_quantity"))), unknown ? "—" : Number(row.L("unsold_quantity", row.L("remaining_quantity"))), acquired > 0 ? (cost / (double)acquired).ToString("N2", Numbers) : "—", unknown ? "—" : Number(cost), unknown ? "—" : Number(row.L("net_sale_proceeds_gp")), unknown ? "—" : Signed(profit), row.S("status_label", row.S("status")));
            var gridRow = profitGrid.Rows[index]; gridRow.Tag = row.S("row_id"); gridRow.DefaultCellStyle.ForeColor = profit > 0 ? Color.FromArgb(20, 122, 58) : profit < 0 ? Color.FromArgb(179, 38, 30) : Color.Black;
            if (row.S("row_id") == selected) gridRow.Selected = true;
        }
        if (firstRow >= 0 && firstRow < profitGrid.Rows.Count) profitGrid.FirstDisplayedScrollingRowIndex = firstRow;
        profitGrid.ResumeLayout();
        profitNote.Text = "Satış GP, GE vergisi sonrası alınan paradır. Kısmi satış maliyeti satılan adede oranlanır. Kalan stok ve banka para hareketleri kâr sayılmaz." + (next.L("generated_at") > 0 ? " Son güncelleme: " + DateTimeOffset.FromUnixTimeSeconds(next.L("generated_at")).ToLocalTime().ToString("HH:mm:ss") : "");
    }
    private JsonObject lastDisplayValues = new();
    private string lastDisplayAccount = "";
    internal void RenderState(JsonObject payload)
    {
        payload = payload.Copy();
        var displayAccount = payload.Obj("state").S("account_key");
        if (displayAccount.Length > 0 && lastDisplayAccount.Length > 0 && displayAccount != lastDisplayAccount) lastDisplayValues = new();
        if (displayAccount.Length > 0) lastDisplayAccount = displayAccount;
        foreach (var key in new[] { "cash_inventory_gp", "active_offers", "slot_capacity", "buy_limits_verified", "canvas_focused", "background_input_supported" })
        {
            if (payload[key] is not null) lastDisplayValues[key] = payload[key]?.DeepClone();
            else if (lastDisplayValues[key] is not null) payload[key] = lastDisplayValues[key]?.DeepClone();
        }
        var portfolio = payload["portfolio"] as JsonObject; var state = payload.Obj("state"); var auto = payload.B("auto"); var mode = payload.S("mode", modeBox.Text);
        var cycle = payload.B("automatic_user_pause") ? "Elle müdahale için bekliyor" : auto ? "Çalışıyor" : "Durduruldu";
        var historyKnown = payload.ContainsKey("buy_limits_verified") || portfolio?.ContainsKey("buy_limits_verified") == true;
        var historyBlocked = historyKnown && auto && mode == "LIVE" && !payload.B("buy_limits_verified", portfolio?.B("buy_limits_verified") ?? false)
            && state.B("logged_in");
        blockerPanel.Visible = historyBlocked;
        if (historyBlocked)
        {
            cycle = "Yeni alışlar engellendi";
            BlockerMessage.Text = "Alış limiti doğrulanmadığı için yeni emir açılmıyor. Kesintide yeni alış yapmadıysan sağdaki düğmeyle doğrula; mevcut teklifler korunur.";
        }
        if (auto && mode == "LIVE" && !state.B("canvas_focused", payload.B("canvas_focused")) && !state.B("background_input_supported", payload.B("background_input_supported"))) cycle += " — RuneLite odağı bekleniyor";
        var uptime = payload.L("cycle_uptime_seconds"); var duration = $"{uptime / 3600:00}:{uptime % 3600 / 60:00}:{uptime % 60:00}";
        var cash = portfolio?.L("free_cash_gp", payload.L("cash_inventory_gp")) ?? payload.L("cash_inventory_gp"); var slots = portfolio?.Arr("offers").Count ?? payload.L("active_offers", state.Objects("offers").Count(o => o.S("state") != "EMPTY"));
        status.Text = $"{mode} | GP: {Number(cash)} | Slot: {slots}/{portfolio?.L("slot_capacity", payload.L("slot_capacity", 3)) ?? payload.L("slot_capacity", 3)} (en çok {payload.L("max_slots_used_this_cycle")}) | Süre: {duration} | Alış limiti: {(portfolio?.B("buy_limits_verified", payload.B("buy_limits_verified")) ?? payload.B("buy_limits_verified") ? "doğrulandı" : "doğrulama gerekiyor")} | {cycle} | {EngineRules.Describe(payload.S("hold_reason"))}";
        if (mode == "LIVE" && !payload.Obj("user_activity").B("monitor_bound")) status.Text += " | Elle müdahale algılayıcısı bağlı değil: ELLE YÖNET kullanılabilir";
        RenderRecovery(payload);
        offersText.Text = J.O(("offers", portfolio?.Arr("offers") ?? state.Arr("offers")), ("saved_history", state.Arr("history")), ("margin", payload.Obj("margin")), ("positions", payload.Arr("positions")), ("total_test_loss_gp", payload.L("total_test_loss")), ("performance", payload.Obj("performance")), ("entry_retry_status", payload.Arr("entry_retry_status"))).ToJsonString(J.Options);
    }
    internal void RenderRecovery(JsonObject payload)
    {
        var reviews = payload.Objects("recovery_reviews").ToList();
        var selected = recoveryGrid.SelectedRows.Count > 0 ? (recoveryGrid.SelectedRows[0].Tag as JsonObject)?.S("trade_id") : null;
        recoveryGrid.SuspendLayout(); recoveryGrid.Rows.Clear();
        foreach (var row in reviews)
        {
            var index = recoveryGrid.Rows.Add(row.S("item_name"), row.S("stage"), Number(row.L("expected_quantity")), Number(row.L("observed_inventory_quantity")), row.S("reason"));
            recoveryGrid.Rows[index].Tag = row.Copy();
            if (row.S("trade_id") == selected) recoveryGrid.Rows[index].Selected = true;
        }
        recoveryGrid.ResumeLayout();
        recoveryNote.Text = reviews.Count == 0 ? "Bekleyen uzlaştırma yok. Elle Collect kullandıysan, doğrulanabilen satış burada görünür."
            : $"{reviews.Count} işlem için uzlaştırma gerekiyor. Teklif, eşya ve bakiye eşleşen elle tahsilatları aşağıdan doğrulayabilirsin. Eksik kanıtla kayıt kapatılmaz.";
        if (reviews.Count > 0 && payload.S("hold_reason") is "RECOVERY_NEEDS_REVIEW" or "MANUAL_REVIEW_OTHER_OFFERS_CONTINUE")
            status.Text += " | Kurtarma: " + string.Join(", ", reviews.Select(r => r.S("item_name")).Distinct()) + " — Kurtarma sekmesini aç";
    }
    private void DrainEvents()
    {
        for (var i = 0; i < 100 && Worker.Events.TryDequeue(out var ev); i++)
        {
            try
            {
                switch (ev.Kind)
                {
                    case "log": Log(ev.Payload.S("text")); break;
                    case "connection": status.Text = "Bağlantı / işlem: " + ev.Payload.S("text"); break;
                    case "control": status.Text = ev.Payload.S("mode") + " | " + EngineRules.Describe(ev.Payload.S("hold_reason")); break;
                    case "market": RenderMarket(ev.Payload.Obj("report")); break;
                    case "profit": RenderProfit(ev.Payload.Obj("report")); break;
                    case "state": case "status": RenderState(ev.Payload); break;
                }
            }
            catch (Exception ex) { Log("Ekran güncellenemedi: " + ex.Message); }
        }
    }
    private void RefreshActivityBinding()
    {
        if (activityMonitor == null || closing) return;
        if (Environment.TickCount64 - lastProcessBinding < 2000 && lastProcessBinding != 0) return;
        lastProcessBinding = Environment.TickCount64;
        activityMonitor.BindProcess(Worker.Mode == "LIVE" ? RuneLiteProcessBinding.Find() : null);
        Worker.SetActivityMonitorState(activityMonitor.IsOperational, activityMonitor.IsBound, activityMonitor.Error);
    }
    private static string Number(long value) => value.ToString("N0", Numbers);
    private static string Signed(long value) => value > 0 ? "+" + Number(value) : Number(value);
    private void Log(string text)
    {
        logText.AppendText(DateTime.Now.ToString("HH:mm:ss ") + text + Environment.NewLine);
        if (logText.TextLength > 500000) logText.Text = logText.Text[^350000..];
        logText.SelectionStart = logText.TextLength; logText.ScrollToCaret();
    }
    private void LaunchBridge()
    {
        try
        {
            var launcher = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Jagex Launcher", "JagexLauncher.exe");
            if (File.Exists(launcher)) { Process.Start(new ProcessStartInfo(launcher) { UseShellExecute = true }); Log("Jagex Launcher açıldı. RuneLite Play ile kayıtlı oturumu başlatın; BAŞLAT oyun içindeki oturum ve GE hazırlığını yürütür."); return; }
            var java = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RuneLite", "jre", "bin", "javaw.exe"); var jar = Path.Combine(folder, "bridge", "build", "libs", "flip-bridge.jar");
            if (!File.Exists(java) || !File.Exists(jar)) throw new FileNotFoundException("RuneLite Java çalışma ortamı veya bridge JAR bulunamadı.");
            var start = new ProcessStartInfo(java) { WorkingDirectory = folder, UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
            foreach (var argument in new[] { "-ea", "-Dflip.directory=" + folder, "-Dflip.allowInput=true", "-jar", jar, "--developer-mode" }) start.ArgumentList.Add(argument);
            Process.Start(start); Log("Bağlantılı RuneLite açıldı. Giriş yapıp GE teklif ekranını açın.");
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Başlatılamadı", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }
    private async void CloseSafely(object? sender, FormClosingEventArgs e)
    {
        if (closeReady) return;
        e.Cancel = true; if (closing) return; closing = true; eventsTimer.Stop(); startTimer.Stop(); activityMonitor?.BindProcess(null); Enabled = false; status.Text = "Program kapanıyor; mevcut işlem sonucu güvenle kaydediliyor.";
        try
        {
            await Task.Run(() => { Worker.Stop(false); Worker.Dispose(); });
            closeReady = true; Close();
        }
        catch (Exception ex) { closing = false; Enabled = true; lastProcessBinding = 0; eventsTimer.Start(); Log("Kapanma tamamlanamadı: " + ex.Message); }
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { activityMonitor?.Dispose(); eventsTimer.Dispose(); startTimer.Dispose(); toolTips.Dispose(); }
        base.Dispose(disposing);
    }
}
