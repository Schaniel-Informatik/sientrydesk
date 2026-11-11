using System.Globalization;
using System.Text;
using SIEntryDesk.Core.Calls;
using SIEntryDesk.Core.Diagnostics;
using SIEntryDesk.Core.Ipc;
using SIEntryDesk.Core.Setup;
using SIEntryDesk.Core.Texts;

namespace SIEntryDesk.Core.Tests;

public class LanguageTests
{
    [Theory]
    [InlineData(null, UiLanguage.De, true)]
    [InlineData("", UiLanguage.De, true)]
    [InlineData("de", UiLanguage.De, true)]
    [InlineData("EN", UiLanguage.En, true)]
    [InlineData(" pl ", UiLanguage.Pl, true)]
    [InlineData("fr", UiLanguage.De, false)]
    public void Language_codes_are_parsed(string? code, UiLanguage expected, bool known)
    {
        Assert.Equal(known, Tr.TryParse(code, out var language));
        Assert.Equal(expected, language);
    }

    [Fact]
    public void Admin_language_is_german_only_for_german()
    {
        using (Tr.Scope(UiLanguage.De))
            Assert.Equal("de", Tr.Admin("de", "en"));
        using (Tr.Scope(UiLanguage.En))
            Assert.Equal("en", Tr.Admin("de", "en"));
        using (Tr.Scope(UiLanguage.Pl))
        {
            Assert.Equal("pl", Tr.User("de", "en", "pl"));
            Assert.Equal("en", Tr.Admin("de", "en"));
        }
    }

    [Theory]
    [InlineData(1, "minuta")]
    [InlineData(2, "minuty")]
    [InlineData(4, "minuty")]
    [InlineData(5, "minut")]
    [InlineData(12, "minut")]
    [InlineData(15, "minut")]
    [InlineData(22, "minuty")]
    [InlineData(30, "minut")]
    [InlineData(60, "minut")]
    [InlineData(112, "minut")]
    [InlineData(124, "minuty")]
    public void Polish_plural_has_three_forms(long n, string expected) =>
        Assert.Equal(expected, Tr.PlPlural(n, "minuta", "minuty", "minut"));

    [Fact]
    public void Windows_language_decides_before_a_configuration_is_known()
    {
        Assert.Equal(UiLanguage.De, Tr.FromCulture(CultureInfo.GetCultureInfo("de-CH")));
        Assert.Equal(UiLanguage.Pl, Tr.FromCulture(CultureInfo.GetCultureInfo("pl-PL")));
        Assert.Equal(UiLanguage.En, Tr.FromCulture(CultureInfo.GetCultureInfo("fr-FR")));
    }

    [Fact]
    public void Dates_follow_the_language()
    {
        var date = new DateTime(2026, 11, 5);
        using (Tr.Scope(UiLanguage.De))
            Assert.Equal("05.11.2026", Tr.Date(date));
        using (Tr.Scope(UiLanguage.En))
            Assert.Equal("2026-11-05", Tr.Date(date));
        using (Tr.Scope(UiLanguage.Pl))
        {
            Assert.Equal("05.11.2026", Tr.Date(date));
            Assert.Equal("2026-11-05", Tr.AdminDate(date));
        }
    }

    [Fact]
    public void Texts_for_users_exist_in_all_three_languages()
    {
        using (Tr.Scope(UiLanguage.En))
        {
            Assert.Equal("after 10 s", RingSettings.Label(RingMode.Delayed));
            Assert.Equal("Answered elsewhere", CallEndReasons.Text(CallEndReason.AnsweredElsewhere));
            Assert.Equal("Ready", TrayStatus.Evaluate(true, new StatusMessage(true, "", "0.10.0", LinkHealth.Ready), null, DateTimeOffset.Now).Text);
        }
        using (Tr.Scope(UiLanguage.Pl))
        {
            Assert.Equal("wył.", RingSettings.Label(RingMode.Off));
            Assert.Equal("Nikt nie odebrał", CallEndReasons.Text(CallEndReason.Timeout));
            Assert.Equal("Gotowe", TrayStatus.Evaluate(true, new StatusMessage(true, "", "0.10.0", LinkHealth.Ready), null, DateTimeOffset.Now).Text);
        }
    }

    [Fact]
    public void Expiry_warning_is_in_the_user_language()
    {
        var options = new EntryDeskOptions { TokenExpires = new DateTime(2026, 11, 20) };
        using (Tr.Scope(UiLanguage.Pl))
            Assert.Equal("Tokeny SI EntryDesk trzeba odnowić do 20.11.2026", options.ExpiryWarning(new DateTime(2026, 11, 11)));
        using (Tr.Scope(UiLanguage.En))
            Assert.Equal("The SI EntryDesk tokens must be renewed by 2026-11-20", options.ExpiryWarning(new DateTime(2026, 11, 11)));
    }

    [Fact]
    public void Configuration_carries_the_language()
    {
        var options = SetupChecks.ParseConfigJson("""{ "Language": "pl", "Host": "console.example", "AccessPin": "x" }""");
        Assert.Equal(UiLanguage.Pl, options.UiLanguage);
        Assert.Null(options.ValidateLanguage());

        var json = SetupChecks.ToConfigJson(options, new DateTime(2026, 11, 11, 10, 0, 0));
        Assert.Contains("\"Language\": \"pl\"", json);
        Assert.Equal(UiLanguage.Pl, SetupChecks.ParseConfigJson(json).UiLanguage);
    }

    [Fact]
    public void Configuration_without_language_is_german_and_unknown_is_reported()
    {
        Assert.Equal(UiLanguage.De, SetupChecks.ParseConfigJson("""{ "Host": "console.example" }""").UiLanguage);

        var unknown = SetupChecks.ParseConfigJson("""{ "Language": "fr", "Host": "console.example" }""");
        Assert.Equal(UiLanguage.De, unknown.UiLanguage);
        Assert.NotNull(unknown.ValidateLanguage());
    }

    [Fact]
    public void Status_of_a_service_before_0_10_has_german()
    {
        var json = """{"type":"status","accessConnected":true,"problem":"","serviceVersion":"0.9.1"}""";
        var status = Assert.IsType<StatusMessage>(IpcProtocol.Deserialize(Encoding.UTF8.GetBytes(json)));
        Assert.Equal("de", status.Language);
    }

    [Fact]
    public void Check_areas_are_identifiers_and_also_understand_the_old_german_ones()
    {
        using (Tr.Scope(UiLanguage.En))
        {
            Assert.Equal("This PC", CheckReport.AreaName(CheckReport.ThisPc));
            Assert.Equal("Installation", CheckReport.AreaName("Anlage"));
        }
        using (Tr.Scope(UiLanguage.De))
            Assert.Equal("Dieser PC", CheckReport.AreaName(CheckReport.ThisPc));
    }
}
