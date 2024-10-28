using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

if (args.Length < 2 || args.Length > 0 && (args[0] == "--help" || args[0] == "-h")) {
	Console.Write("Expected arguments: [terraformPlanFileName] [terraformPolicyFileName1] [terraformPolicyFileName2] [terraformPolicyFileName3]...");
	return 2;
}
var terraformPlanFileName = args[0];
var terraformPolicyFileNames = args.Skip(1);
var planStream = new FileStream(terraformPlanFileName, FileMode.Open, FileAccess.Read);
var inputPolicies = terraformPolicyFileNames.Select(GetPolicy).ToArray();
var policy = new Policy {
	permittedCreates = inputPolicies.SelectMany(i => i.permittedCreates ?? new PolicyElement[0]).ToArray(),
	permittedDeletes = inputPolicies.SelectMany(i => i.permittedDeletes ?? new PolicyElement[0]).ToArray(),
	permittedUpdates = inputPolicies.SelectMany(i => i.permittedUpdates ?? new PolicyElement[0]).ToArray(),
	permittedDrifts = inputPolicies.SelectMany(i => i.permittedDrifts ?? new PolicyElement[0]).ToArray(),
};
[System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("ReflectionAnalysis", "IL2026:RequiresUnreferencedCode", Justification = "Supplying JsonSerializerOptions with TypeInfoResolver should suppress this warning. This appears to be a bug?")]
static Policy GetPolicy(string terraformPolicyFileName) {
	var policyStream = new FileStream(terraformPolicyFileName, FileMode.Open, FileAccess.Read);
	return JsonSerializer.Deserialize<Policy>(policyStream, new JsonSerializerOptions { AllowTrailingCommas = true, ReadCommentHandling = JsonCommentHandling.Skip, IncludeFields = true, TypeInfoResolver = SourceGenerationContext.Default })!;
}
//Console.WriteLine(J(policy));

var document = AssertNotNull(System.Text.Json.Nodes.JsonNode.Parse(planStream, null, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }));
var failed = false;

var changes = FindChild(document, "resource_changes")?.AsArray().ToArray() ?? new JsonNode[0];
foreach (var drift in FindChild(document, "resource_drift")?.AsArray().ToArray() ?? new JsonNode[0]) {
	AssertNotNull(drift);
	var address = AssertNotNull(AssertNotNull(drift["address"]).GetValue<string>());
	var type = AssertNotNull(AssertNotNull(drift["type"]).GetValue<string>());
	var change = AssertNotNull(FindChild(drift, "change"));
	var before = AssertNotNull(FindChild(change, "before"));
	var after = AssertNotNull(FindChild(change, "after"));
	var actions = GetStringArray(AssertNotNull(FindChild(change, "actions")));
	if (!Enumerable.SequenceEqual(actions, new[] { "update" })) {
		Console.WriteLine($"Drift unknown action: {string.Join(", ", actions)}");
		failed = true;
		continue;
	}


	var diffs = Diff(type, "", before, after);
	var policies = GetPolicyElements(policy.permittedDrifts, address, type).SelectMany(i => i.permittedUpdates ?? new PolicyItem[0]).ToArray();
	//Console.WriteLine(J(policies));
	Console.WriteLine($"Drift diff: {address} - {type}");
	if (changes.Any(i => AssertNotNull(AssertNotNull(i)["address"]).GetValue<string>() == address && GetStringArray(AssertNotNull(FindChild(AssertNotNull(FindChild(i, "change")), "actions"))).Contains("delete"))) {
		Console.WriteLine($"Ignoring drift on resource being deleted");
		continue;
	}
	foreach (var diff in diffs) {
		Console.WriteLine($"Drift diff: {diff.address} - {diff.before} != {diff.after}");
		var allowed = policies.Any(i => Regex.IsMatch(diff.address, MakeAllStringMatch(i.addressRegex)) && (i.beforeRegex == null || Regex.IsMatch(diff.before, MakeAllStringMatch(i.beforeRegex))) && (i.afterRegex == null || Regex.IsMatch(diff.after, MakeAllStringMatch(i.afterRegex))));

		if (allowed) {
			Console.WriteLine($"   Allowed");
		} else {
			Console.WriteLine($"   DENIED");
			failed = true;
		}
	}
}

foreach (var changeI in changes) {
	AssertNotNull(changeI);
	var address = AssertNotNull(AssertNotNull(changeI["address"]).GetValue<string>());
	var type = AssertNotNull(AssertNotNull(changeI["type"]).GetValue<string>());
	var change = AssertNotNull(FindChild(changeI, "change"));
	var actions = GetStringArray(AssertNotNull(FindChild(change, "actions")));

	if (Enumerable.SequenceEqual(actions, new[] { "no-op" }) || Enumerable.SequenceEqual(actions, new[] { "read" })) {
		continue;
	}
	if (actions.Contains("update")) {
		Console.WriteLine($"Update: {address} - {type}");
		var policies = GetPolicyElements(policy.permittedUpdates, address, type).ToArray();
		if (!policies.Any()) {
			Console.WriteLine($"     DENIED");
			failed = true;
		} else {
			var before = AssertNotNull(FindChild(change, "before"));
			var after = AssertNotNull(FindChild(change, "after"));
			var diffs = Diff(type, "", before, after);
			var itemPolicies = policies.SelectMany(i => i.permittedUpdates ?? new PolicyItem[0]);
			foreach (var diff in diffs) {
				Console.WriteLine($"Update diff: {diff.address} - {diff.before} != {diff.after}");
				var allowed = itemPolicies.Any(i => ElementMatch(diff, i, before, after));

				if (allowed) {
					Console.WriteLine($"   Allowed");
				} else {
					Console.WriteLine($"   DENIED");
					failed = true;
				}
			}
		}
	} else if (actions.Contains("create")) {
		Console.WriteLine($"Create: {address} - {type}");
		var policies = GetPolicyElements(policy.permittedCreates, address, type).ToArray();
		if (!policies.Any()) {
			Console.WriteLine($"     DENIED");
			failed = true;
		} else {
			Console.WriteLine($"     Allowed");
		}
	} else if (actions.Contains("delete")) {
		Console.WriteLine($"Delete: {address} - {type}");
		var policies = GetPolicyElements(policy.permittedDeletes, address, type).ToArray();
		if (!policies.Any()) {
			Console.WriteLine($"     DENIED");
			failed = true;
		} else {
			Console.WriteLine($"     Allowed");
		}
	} else {
		Console.WriteLine($"Drift unknown action: {string.Join(", ", actions)}");
		failed = true;
	}
}

Console.WriteLine($"Final Outcome: {(failed ? "DENIED" : "Allowed")}");
return failed ? 1 : 0;

//static object MaskSensitive(string path, JsonNode input, JsonNode sensitiveSource) {
//	var sensitiveMask = FindElement(sensitiveSource, path);
//	if (sensitiveMask == null) {
//		return input;
//	}
//	var masked = input.Clone();
//	input.TryGetProperty
//	if (sensitiveMask.Value.ValueKind == JsonValueKind.True) {
//	}
//	return input;
//}

static bool ElementMatch(DiffItem diffItem, PolicyItem policyItem, JsonNode beforeRoot, JsonNode afterRoot) {
	var matchA = Regex.Match(diffItem.address, MakeAllStringMatch(policyItem.addressRegex));
	var match = matchA.Success && (policyItem.beforeRegex == null || Regex.IsMatch(diffItem.before, MakeAllStringMatch(policyItem.beforeRegex))) && (policyItem.afterRegex == null || Regex.IsMatch(diffItem.after, MakeAllStringMatch(policyItem.afterRegex)));
	if (match) {
		foreach (var whenItem in policyItem.when ?? new PolicyItemWhen[0]) {
			var address = Regex.Replace(whenItem.address, "\\$(\\d+)", (m) => {
				return matchA.Groups[int.Parse(m.Groups[1].Value)].Value;
			});
			var beforeElement = AssertNotNull(FindElement(beforeRoot, address));
			var afterElement = AssertNotNull(FindElement(afterRoot, address));
			match = (whenItem.beforeRegex == null || Regex.IsMatch(beforeElement.ToString()!, MakeAllStringMatch(whenItem.beforeRegex))) && (whenItem.afterRegex == null || Regex.IsMatch(afterElement.ToString()!, MakeAllStringMatch(whenItem.afterRegex)));
			if (!match) {
				Console.WriteLine($"When: DIDN'T match {address}: {beforeElement.ToString()!} != {whenItem.beforeRegex} or {afterElement.ToString()!} != {whenItem.afterRegex}");
				break;
			} else {
				Console.WriteLine($"When: Match {address}: {beforeElement.ToString()!} == {whenItem.beforeRegex} and {afterElement.ToString()!} == {whenItem.afterRegex}");
			}
		}
	}
	return match;
}
static JsonNode? FindElement(JsonNode root, string path) {
	if (path == "") {
		return root;
	}

	var nextIndex = path.IndexOfAny(new[] { '.', '[', ']' }, 1);
	var key = nextIndex == -1 ? path.Substring(1) : path.Substring(1, nextIndex - 1);
	if (path[0] == '.') {
		var remainder = nextIndex == -1 ? "" : path.Substring(nextIndex);
		return FindElement(AssertNotNull(FindChild(root, key)), remainder);
	} else if (path[0] == '[') {
		var remainder = nextIndex == -1 ? "" : path.Substring(nextIndex + 1);
		return FindElement(AssertNotNull(FindChildArray(root, int.Parse(key))), remainder);
	} else {
		return null;
	}
}
static PolicyElement[] GetPolicyElements(PolicyElement[]? list, string address, string type) {
	if (list == null) {
		return new PolicyElement[0];
	}

	return list.Where(i => Regex.IsMatch(address, MakeAllStringMatch(i.addressRegex)) && Regex.IsMatch(type, MakeAllStringMatch(i.typeRegex))).ToArray();
}

static string MakeAllStringMatch(string regex) => $"^(?:{regex})$";
//static string J(object o) => JsonSerializer.Serialize(o, new JsonSerializerOptions { AllowTrailingCommas = true, ReadCommentHandling = JsonCommentHandling.Skip, IncludeFields = true, WriteIndented = true });
static JsonNode? FindChild(JsonNode el, string name) {
	return el.AsObject()[name];
};

static JsonNode? FindChildArray(JsonNode el, int index) {
	return el.AsArray().Count < index ? el.AsArray()[index] : null;
};
static string[] GetStringArray(JsonNode el) => el.AsArray().Select(i => AssertNotNull(i).AsValue().GetValue<string>()).ToArray();
static T AssertNotNull<T>([System.Diagnostics.CodeAnalysis.NotNull] T? input, [System.Runtime.CompilerServices.CallerArgumentExpressionAttribute(nameof(input))] string inputStr = null!) {
	if (input == null) {
		throw new ArgumentNullException(inputStr);
	}
	return input;
}
//[System.Diagnostics.CodeAnalysis.DoesNotReturn]
//static void ThrowE(string label, string address) => throw new Exception($"{label} at {address}");

static IEnumerable<DiffItem> Diff(string type, string address, JsonNode? before, JsonNode? after) {
	if (before == null || after == null || before.GetValueKind() != after.GetValueKind()) {
		if (before == null) {
			if (after == null) {
			} else {
				yield return new DiffItem(address, "NULL", AssertNotNull(after.ToString()));
			}
		} else if (after == null) {
			yield return new DiffItem(address, AssertNotNull(before.ToString()), "NULL");
		} else {
			yield return new DiffItem(address, before.GetValueKind().ToString(), after.GetValueKind().ToString());
		}
		yield break;
	}
	var beforeV = before;
	var afterV = after;
	if (type == "helm_release" && address == ".set") {
		static string getName(JsonNode? i) => AssertNotNull(FindChild(AssertNotNull(i), "name")).AsValue().GetValue<string>();
		var beforeItemsD = beforeV.AsArray().ToArray().ToLookup(getName).ToDictionary(i => i.Key, i => i.ToArray());
		var afterItemsD = afterV.AsArray().ToArray().ToLookup(getName).ToDictionary(i => i.Key, i => i.ToArray());
		var keys = beforeItemsD.Concat(afterItemsD).Select(i => i.Key).Distinct();
		foreach (var key in keys) {
			var beforeItems = (beforeItemsD.TryGetValue(key, out var a1) ? a1 : null) ?? new JsonNode[0];
			var afterItems = (afterItemsD.TryGetValue(key, out var a2) ? a2 : null) ?? new JsonNode[0];
			var count = Math.Max(beforeItems.Length, afterItems.Length);
			for (var i = 0; i < count; i++) {
				foreach (var j in Diff(type, $"{address}[{key}][{i}]", i < beforeItems.Length ? beforeItems[i] : null, i < afterItems.Length ? afterItems[i] : null)) { yield return j; }
			}
		}
	} else if (beforeV is JsonArray beforeA) {
		var beforeItems = beforeA.ToArray();
		var afterItems = afterV.AsArray().ToArray();
		var count = Math.Max(beforeItems.Length, afterItems.Length);
		for (var i = 0; i < count; i++) {
			foreach (var j in Diff(type, $"{address}[{i + 1}]", i < beforeItems.Length ? beforeItems[i] : null, i < afterItems.Length ? afterItems[i] : null)) { yield return j; }
		}
	} else if (beforeV is JsonObject beforeO ) {
		var beforeItems = beforeO.ToDictionary(i => i.Key, i => i.Value);
		var afterItems = afterV.AsObject().ToDictionary(i => i.Key, i => i.Value);;
		var keys = beforeItems.Keys.Union(afterItems.Keys);
		foreach (var key in keys) {
			foreach (var j in Diff(type, $"{address}.{key}", beforeItems.TryGetValue(key, out var bI) ? bI : null, afterItems.TryGetValue(key, out var aI) ? aI : null)) { yield return j; }
		}
	} else {
		var beforeStr = beforeV.ToString();
		var afterStr = afterV.ToString();
		if (beforeStr != afterStr) {
			yield return new DiffItem(address, beforeStr, afterStr);
		}
	}
}

internal record struct DiffItem(string address, string before, string after) { }

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(Policy))]
internal partial class SourceGenerationContext : JsonSerializerContext { }

public class Policy {
	public PolicyElement[]? permittedDrifts;
	public PolicyElement[]? permittedDeletes;
	public PolicyElement[]? permittedCreates;
	public PolicyElement[]? permittedUpdates;
}

public class PolicyElement {
	public required string addressRegex;
	public required string typeRegex;
	public PolicyItem[]? permittedUpdates;
}

public class PolicyItem {
	public required string addressRegex;
	public string? beforeRegex;
	public string? afterRegex;
	public PolicyItemWhen[]? when;
}
public class PolicyItemWhen {
	public required string address;
	public string? beforeRegex;
	public string? afterRegex;
}
