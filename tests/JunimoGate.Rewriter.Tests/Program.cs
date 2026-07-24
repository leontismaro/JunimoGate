using JunimoGate.Rewriter;
using JunimoGate.Tests;

var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "junimogate-rewriter-tests"));
var input = Path.Combine(root, "input", "StardewValley.dll");
var output = Path.Combine(root, "staging", "StardewValley.dll");
var recipe = new RewriteRecipeIdentity("android-activity-bridge", "1");

return TestHarness.Run(
    ("Rewrite request accepts absolute input and staging output", () =>
    {
        var request = new RewriteRequest(input, output, recipe);
        TestHarness.Equal(input, request.InputAssemblyPath);
        TestHarness.Equal(output, request.StagingOutputPath);
        TestHarness.Equal("android-activity-bridge@1", request.Recipe.ToString());
    }),
    ("Rewrite request rejects relative input", () =>
    {
        TestHarness.Throws<ArgumentException>(() => new RewriteRequest("StardewValley.dll", output, recipe));
    }),
    ("Rewrite request rejects relative staging output", () =>
    {
        TestHarness.Throws<ArgumentException>(() => new RewriteRequest(input, "staging/StardewValley.dll", recipe));
    }),
    ("Rewrite request rejects in-place output", () =>
    {
        TestHarness.Throws<ArgumentException>(() => new RewriteRequest(input, input, recipe));
    }),
    ("Rewrite recipe identity is required and non-empty", () =>
    {
        TestHarness.Throws<ArgumentException>(() => new RewriteRecipeIdentity("", "1"));
        TestHarness.Throws<ArgumentException>(() => new RewriteRecipeIdentity("recipe", ""));
        TestHarness.Throws<ArgumentNullException>(() => new RewriteRequest(input, output, null!));
    }));
