using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Http;
using System.Security.Claims;
using Platform.Api.Features.VersionControl;
using Microsoft.Extensions.Configuration;
using Platform.Api.Features.Lineage;
using Platform.Api.Features.VersionControl.Query;
using Platform.Api.Features.Rendering;
using Platform.Contracts.Rendering;
using Platform.Contracts.VersionControl;
using System.Text;

var asset = Guid.NewGuid();
var hidden = Commit("hidden", ["root"], true);
var visible = Commit("visible", ["hidden"], false);
var refs = new[] { new RefView("main", "branch", "visible", true, 2), new RefView("favorite", "tag", "visible", false, 1) };
var fetched = new[] { visible, Commit("root", [], false) };
var page = LineageProjection.Build(asset, "main", "visible", false, 0, 1, fetched, refs, new HashSet<string> { "hidden" });
Assert(page.Nodes.Count == 1 && page.NextOffset == 1, "History must be bounded and continue at the next page.");
Assert(page.Nodes[0].HiddenParentIds.SequenceEqual(["hidden"]), "Normal history must identify collapsed hidden ancestry.");
Assert(page.Nodes[0].Branches.SequenceEqual(["main"]) && page.Nodes[0].Tags.SequenceEqual(["favorite"]), "Branch and tag labels must match exact commit IDs.");
Assert(page.Nodes[0].Commit.Parents.SequenceEqual(["hidden"]), "Projection must retain canonical parent IDs.");
var revealed = LineageProjection.Build(asset, "main", "visible", true, 0, 1, [hidden], refs, new HashSet<string> { "hidden" });
Assert(revealed.Nodes[0].Commit.Hidden && revealed.Nodes[0].HiddenParentIds.Count == 0, "Show-hidden must reveal exact hidden identity without false collapsed markers.");

var edit = Guid.NewGuid();
var root = new GraphCommit("root", [], "owner", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, "root", [], false);
var target = new GraphCommit("target", ["root"], "owner", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, "target",
    [new ChangeView(asset, edit, "added", null, new string('a', 64), "image.raw", "active")], false);
var source = new GraphCommit("source", ["root"], "owner", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, "source",
    [new ChangeView(asset, edit, "added", null, new string('b', 64), "image.raw", "active")], false);
var graph = new Dictionary<string, GraphCommit> { ["root"] = root, ["target"] = target, ["source"] = source };
var comparison = LineageComparison.Build(graph, asset, "target", "source");
Assert(comparison.BaseCommitId == "root" && comparison.TargetCommitId == "target" && comparison.SourceCommitId == "source", "Comparison must bind exact commits and common ancestor.");
Assert(comparison.TargetStates.Single().XmpSha256 == new string('a', 64) && comparison.SourceStates.Single().XmpSha256 == new string('b', 64), "Comparison must identify each side's exact XMP.");
Assert(comparison.TargetStates.Single().PreviewState == "not_requested" && comparison.SourceStates.Single().PreviewState == "not_requested", "Preview requests must remain independent of comparison truth.");
Assert(comparison.Differences.Single().AssetId == asset, "Only selected-image differences may appear.");
var selectedState = comparison.TargetStates.Single();
var previewId = Guid.NewGuid();
Assert(LineageComparison.WithPreviewStatus(selectedState, new RenditionStatusView(previewId, null, RenderState.Pending, false, null, null)).PreviewState == "pending", "Pending preview must not imply a rendition exists.");
Assert(LineageComparison.WithPreviewStatus(selectedState, new RenditionStatusView(previewId, null, RenderState.Failed, false, null, "renderer_timeout")).PreviewState == "failed", "Failed preview must remain distinct from comparison data.");
Assert(LineageComparison.WithPreviewStatus(selectedState, new RenditionStatusView(previewId, null, RenderState.Completed, false, null, null)).PreviewState == "unavailable", "A completed preview without provenance must not be shown.");
var wrongProvenance = new RenderProvenance(new string('c', 64), new string('b', 64), "source", "5.6.1", "unapproved", "render-v1", "rendering-v1", DateTimeOffset.UnixEpoch);
Assert(LineageComparison.WithPreviewStatus(selectedState, new RenditionStatusView(previewId, null, RenderState.Completed, false, wrongProvenance, null)).PreviewState == "unavailable", "A different version's preview must never be substituted.");
Assert(LineageComparison.Build(graph, Guid.NewGuid(), "target", "source").TargetStates.Count == 0, "Absent image state must remain explicit.");
var unrelated = new GraphCommit("unrelated", [], "owner", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, "other", [], false);
graph.Add("unrelated", unrelated);
Assert(LineageComparison.Build(graph, asset, "target", "unrelated").BaseCommitId is null, "Disconnected histories must report no common ancestor.");

var xmpBase = Xmp("0", "base", "");
var xmpTarget = Xmp("4", "base", "");
var xmpSource = Xmp("0", "source", "");
var disjoint = XmpMergePolicy.Assess(xmpBase, xmpTarget, xmpSource);
Assert(XmpMergePolicy.Compare(xmpTarget, xmpSource).Fields.Any(field => field.Field == "rating") &&
       XmpMergePolicy.Compare(xmpTarget, xmpSource).Fields.Any(field => field.Field == "title"),
    "Two-version comparison must expose recognized field values without requiring a common base.");
Assert(disjoint.CanCompose && disjoint.Fields.Count == 2 && disjoint.Fields.All(field => !field.RequiresResolution),
    "Disjoint recognized metadata changes should compose automatically.");
var merged = XmpMergePolicy.Compose(xmpBase, xmpTarget, xmpSource, new Dictionary<string, string>());
Assert(Encoding.UTF8.GetString(merged).Contains("Rating=\"4\"") && Encoding.UTF8.GetString(merged).Contains(">source</rdf:li>"),
    "Composed XMP must contain both independently changed values.");
Assert(merged.SequenceEqual(XmpMergePolicy.Compose(xmpBase, xmpTarget, xmpSource, new Dictionary<string, string>())),
    "The exact generated bytes must be deterministic.");
var overlap = XmpMergePolicy.Assess(xmpBase, xmpTarget, Xmp("2", "base", ""));
Assert(overlap.Fields.Single().RequiresResolution, "Concurrent scalar edits require owner resolution.");
Assert(XmpMergePolicy.Assess(xmpBase, xmpTarget, Xmp("0", "base", "<darktable:history>edit</darktable:history>"))
    .Fields.Any(field => field.Field == "opaque" && field.RequiresResolution), "History or unknown darktable changes must require resolution.");
Assert(XmpMergePolicy.Assess(xmpBase, xmpTarget, Xmp("0", "base", "<darktable:masks_history>mask</darktable:masks_history>"))
    .Fields.Any(field => field.Field == "opaque" && field.RequiresResolution), "Mask changes must require resolution.");
Assert(!XmpMergePolicy.Assess(null, xmpTarget, xmpSource).CanCompose, "A missing common base cannot auto-merge.");
Assert(!XmpMergePolicy.Assess(xmpBase, xmpTarget, Encoding.UTF8.GetBytes("<!DOCTYPE foo><x/>")) .CanCompose,
    "Malformed or DTD input must fail closed.");
var now = DateTimeOffset.Parse("2026-09-26T12:00:00Z");
var recent = new DefaultHttpContext();
recent.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("auth_time", now.AddMinutes(-4).ToUnixTimeSeconds().ToString())], "fixture"));
RecentAuthentication.Require(recent, new FixedClock(now));
recent.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("auth_time", now.AddMinutes(-6).ToUnixTimeSeconds().ToString())], "fixture"));
RejectRecent(recent, now);
var disabled = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    { ["Lineage:MutationsEnabled"] = "false" }).Build();
Assert(LineageMutationGate.Blocked(disabled, "POST", "/v1/libraries/lib/lineage/merges/confirm"),
    "The operations switch must stop merge confirmation.");
Assert(!LineageMutationGate.Blocked(disabled, "POST", "/v1/libraries/lib/lineage/merges/review") &&
       !LineageMutationGate.Blocked(disabled, "GET", "/v1/libraries/lib/lineage/images/id/history"),
    "The operations switch must preserve read-only lineage and review.");
recent.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("auth_time", now.AddMinutes(1).ToUnixTimeSeconds().ToString())], "fixture"));
RejectRecent(recent, now);
recent.User = new ClaimsPrincipal(new ClaimsIdentity([], "fixture"));
RejectRecent(recent, now);
var seven = "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\" xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\" xmlns:xmp=\"http://ns.adobe.com/xap/1.0/\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\" xmlns:exif=\"http://ns.adobe.com/exif/1.0/\" xmlns:darktable=\"http://darktable.sf.net/\"><rdf:RDF><rdf:Description xmp:Rating=\"0\" darktable:rejected=\"false\" exif:GPSLatitude=\"1,0N\" exif:GPSLongitude=\"1,0E\"><darktable:colorlabels><rdf:Seq><rdf:li>0</rdf:li></rdf:Seq></darktable:colorlabels><dc:subject><rdf:Seq><rdf:li>base</rdf:li></rdf:Seq></dc:subject><dc:title><rdf:Alt><rdf:li>base</rdf:li></rdf:Alt></dc:title><dc:description><rdf:Alt><rdf:li>base</rdf:li></rdf:Alt></dc:description></rdf:Description></rdf:RDF></x:xmpmeta>";
foreach (var (field, oldValue, newValue) in new (string Field, string Old, string New)[]
{
    ("rating", "Rating=\"0\"", "Rating=\"2\""),
    ("rejected", "rejected=\"false\"", "rejected=\"true\""),
    ("color", "<rdf:li>0</rdf:li>", "<rdf:li>2</rdf:li>"),
    ("tags", "<rdf:li>base</rdf:li></rdf:Seq></dc:subject>", "<rdf:li>tag</rdf:li></rdf:Seq></dc:subject>"),
    ("title", "<rdf:li>base</rdf:li></rdf:Alt></dc:title>", "<rdf:li>title</rdf:li></rdf:Alt></dc:title>"),
    ("description", "<rdf:li>base</rdf:li></rdf:Alt></dc:description>", "<rdf:li>description</rdf:li></rdf:Alt></dc:description>"),
    ("location", "GPSLatitude=\"1,0N\"", "GPSLatitude=\"2,0N\"")
})
{
    var assessment = XmpMergePolicy.Assess(Encoding.UTF8.GetBytes(seven), Encoding.UTF8.GetBytes(seven.Replace(oldValue, newValue)), Encoding.UTF8.GetBytes(seven));
    Assert(assessment.Fields.Count == 1 && assessment.Fields[0].Field == field && !assessment.Fields[0].RequiresResolution,
        $"Recognized field {field} must be independently detected.");
}

await using (var app = WebApplication.CreateBuilder().Build())
{
    LineageEndpoints.Map(app);
    var route = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>()
        .Single(endpoint => endpoint.RoutePattern.RawText == "/v1/libraries/{library}/lineage/images/{asset:guid}/history");
    Assert(route.Metadata.GetMetadata<IAuthorizeData>() is not null, "History route must require authentication.");
    var compare = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>()
        .Single(endpoint => endpoint.RoutePattern.RawText == "/v1/libraries/{library}/lineage/images/{asset:guid}/compare");
    Assert(compare.Metadata.GetMetadata<IAuthorizeData>() is not null, "Comparison route must require authentication.");
}

Console.WriteLine("Lineage projection and route checks passed.");
await LineageIntegrationChecks.RunAsync();

static CommitView Commit(string id, IReadOnlyList<string> parents, bool hidden) =>
    new(id, parents, "owner", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, id, [], hidden);

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static void RejectRecent(DefaultHttpContext context, DateTimeOffset now)
{
    try { RecentAuthentication.Require(context, new FixedClock(now)); }
    catch (VersionControlProblem problem) when (problem.Code == "recent_authentication_required") { return; }
    throw new InvalidOperationException("Privileged action must require a signed recent auth_time claim.");
}

static byte[] Xmp(string rating, string title, string extra) => Encoding.UTF8.GetBytes(
    $"<x:xmpmeta xmlns:x=\"adobe:ns:meta/\" xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\" xmlns:xmp=\"http://ns.adobe.com/xap/1.0/\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\" xmlns:darktable=\"http://darktable.sf.net/\"><rdf:RDF><rdf:Description xmp:Rating=\"{rating}\"><dc:title><rdf:Alt><rdf:li xml:lang=\"x-default\">{title}</rdf:li></rdf:Alt></dc:title>{extra}</rdf:Description></rdf:RDF></x:xmpmeta>");

sealed class FixedClock(DateTimeOffset value) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => value;
}
