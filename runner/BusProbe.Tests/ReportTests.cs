using BusProbe;
using Xunit;

namespace BusProbe.Tests;

// 결과서·추적표 생성 테스트
public class ReportTests
{
    static CaseResult Case(string id, string title, string status, params string[] reqs) =>
        new(new Scenario { Id = id, Title = title, Requirements = reqs.ToList() }, "", status, 1, new(), new(), null);
    static readonly RunInfo Run = new(new DateTime(2026, 11, 22, 0, 0, 0, DateTimeKind.Utc), null, "0.1.0");

    [Fact]
    public void Csv_ListsUncoveredRequirementAsNotTested()
    {
        var csv = Report.Csv("r1", new() { Case("SC-1", "t", "PASS", "REQ-1") }, new() { new("REQ-1", "첫째"), new("REQ-2", "둘째, 쉼표") });
        Assert.Contains("REQ-1,첫째,SC-1,PASS,r1", csv);
        Assert.Contains("REQ-2,\"둘째, 쉼표\",,미시험,r1", csv);
    }

    [Fact]
    public void Csv_KeepsRequirementMissingFromFile()
    {
        var csv = Report.Csv("r1", new() { Case("SC-1", "t", "FAIL", "REQ-X") }, new());
        Assert.Contains("REQ-X,(요구사항 파일에 없음),SC-1,FAIL", csv);
    }

    [Fact]
    public void Html_EscapesScriptEndTagInTitle()
    {
        var html = Report.Html(Run, new() { Case("SC-1", "</script><img src=x onerror=alert(1)>", "PASS") }, new());
        Assert.DoesNotContain("</script><img", html);
        Assert.Contains("\u003c/script>", html);
    }

    [Fact]
    public void Html_ContainsRunSummary()
    {
        var html = Report.Html(Run, new() { Case("SC-1", "a", "PASS"), Case("SC-2", "b", "FAIL") }, new());
        Assert.Contains("\"total\":2", html);
        Assert.Contains("\"failed\":1", html);
    }
}

// DB 연결 문자열 변환 테스트 (실제 연결 없음)
public class StoreTests
{
    [Fact]
    public void ConnString_ConvertsNeonUriAndRequiresSsl()
    {
        var cs = PgStore.ConnString("postgresql://app:p%40ss@ep-cool-123.ap-southeast-1.aws.neon.tech/neondb?sslmode=require&channel_binding=require");
        Assert.Contains("Host=ep-cool-123.ap-southeast-1.aws.neon.tech", cs);
        Assert.Contains("Database=neondb", cs);
        Assert.Contains("Username=app", cs);
        Assert.Contains("Password=p@ss", cs);
        Assert.Contains("SSL Mode=Require", cs);
    }

    [Fact]
    public void ConnString_PassesNpgsqlFormatThrough() =>
        Assert.Equal("Host=h;Database=d", PgStore.ConnString("Host=h;Database=d"));
}
