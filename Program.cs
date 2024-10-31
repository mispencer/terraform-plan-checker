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
	permittedUpdates = inputPolicies.SelectMany(i => i.permittedUpdates ?? new PolicyElementUpdate[0]).ToArray(),
	permittedDrifts = inputPolicies.SelectMany(i => i.permittedDrifts ?? new PolicyElementUpdate[0]).ToArray(),
	permittedDriftDeletes = inputPolicies.SelectMany(i => i.permittedDriftDeletes ?? new PolicyElement[0]).ToArray(),
};
[System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("ReflectionAnalysis", "IL2026:RequiresUnreferencedCode", Justification = "Supplying JsonSerializerOptions with TypeInfoResolver should suppress this warning. This appears to be a bug?")]
static Policy GetPolicy(string terraformPolicyFileName) {
	var policyStream = new FileStream(terraformPolicyFileName, FileMode.Open, FileAccess.Read);
	return JsonSerializer.Deserialize<Policy>(policyStream, new JsonSerializerOptions { AllowTrailingCommas = true, ReadCommentHandling = JsonCommentHandling.Skip, IncludeFields = true, TypeInfoResolver = SourceGenerationContext.Default })!;
}

[System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("ReflectionAnalysis", "IL2026:RequiresUnreferencedCode", Justification = "String is fine to reference")]
static void ReplaceNodeWithString(JsonValue node, string value) {
	node.ReplaceWith(JsonValue.Create(value));
}
//Console.WriteLine(J(policy));

var document = AssertNotNull(System.Text.Json.Nodes.JsonNode.Parse(planStream, null, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }));
var failed = false;

var changes = FindChild(document, "resource_changes")?.AsArray().ToArray() ?? new JsonNode[0];
foreach (var (drift,index) in (FindChild(document, "resource_drift")?.AsArray().ToArray() ?? new JsonNode[0]).Select((i,j) => (i,j))) {
	try {
		AssertNotNull(drift);
		var address = AssertNotNull(AssertNotNull(drift["address"]).GetValue<string>());
		var type = AssertNotNull(AssertNotNull(drift["type"]).GetValue<string>());
		var change = AssertNotNull(FindChild(drift, "change"));
		var actions = GetStringArray(AssertNotNull(FindChild(change, "actions")));
		Console.WriteLine($"Drift diff: {address} - {type}");
		if (Enumerable.SequenceEqual(actions, new[] { "update" })) {
			var before = AssertNotNull(FindChild(change, "before"));
			var after = AssertNotNull(FindChild(change, "after"));
			var beforeSensitive = AssertNotNull(FindChild(change, "before_sensitive"));
			var afterSensitive = AssertNotNull(FindChild(change, "after_sensitive"));
			var diffs = Diff(type, "", before, after, beforeSensitive, afterSensitive);
			var policies = GetPolicyElements(policy.permittedDrifts, address, type).SelectMany(i => i.permittedUpdates ?? new PolicyItem[0]).ToArray();
			//Console.WriteLine(J(policies));
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
		} else if (Enumerable.SequenceEqual(actions, new[] { "delete" })) {
			var policies = GetPolicyElements(policy.permittedDriftDeletes, address, type).ToArray();
			if (!policies.Any()) {
				Console.WriteLine($"     DENIED");
				failed = true;
			} else {
				Console.WriteLine($"     Allowed");
			}
		} else {
			Console.WriteLine($"Drift unknown action: {string.Join(", ", actions)}");
			failed = true;
			continue;
		}
	} catch (Exception e) {
		throw new Exception($"Error on draft {index}", e);
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
			var beforeSensitive = AssertNotNull(FindChild(change, "before_sensitive"));
			var afterSensitive = AssertNotNull(FindChild(change, "after_sensitive"));
			var diffs = Diff(type, "", before, after, beforeSensitive, afterSensitive);
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

static JsonNode MaskSensitive(JsonNode input, JsonNode sensitiveMask) {
	if (sensitiveMask == null) {
		return input;
	}
	var masked = input.DeepClone();
	void WalkAndMask(JsonNode input, JsonNode? sensitiveMask) {
		if (input is JsonObject inputO) {
			if (sensitiveMask != null && sensitiveMask is JsonObject sensitiveMaskO) {
				foreach(var inputI in inputO.ToArray()) {
					if (inputI.Value != null) {
						var sensitiveMaskI = sensitiveMaskO[inputI.Key];
						WalkAndMask(inputI.Value, sensitiveMaskI);
					}
				}
			}
		} else if (input is JsonArray inputA) {
			if (sensitiveMask != null && sensitiveMask is JsonArray sensitiveMaskA) {
				for(var index = 0; index < inputA.Count; index++) {
					var inputI = inputA[index];
					if (inputI != null) {
						var sensitiveMaskI = index < sensitiveMaskA.Count ? sensitiveMaskA[index] : null;
						WalkAndMask(inputI, sensitiveMaskI);
					}
				}
			}
		} else if (input is JsonValue inputV) {
			if (sensitiveMask != null && sensitiveMask is JsonValue sensitiveMaskV && sensitiveMaskV.TryGetValue<bool>(out var sensitiveMaskB) && sensitiveMaskB) {
				ReplaceNodeWithString(inputV, "(sensitive value)");
			}
		}

	}
	if (sensitiveMask != null) {
		WalkAndMask(input, sensitiveMask);
	}
	return input;
}

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
static T[] GetPolicyElements<T>(T[]? list, string address, string type) where T : PolicyElement {
	if (list == null) {
		return new T[0];
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

static IEnumerable<DiffItem> Diff(string type, string address, JsonNode? before, JsonNode? after, JsonNode? beforeSensitive, JsonNode? afterSensitive) {
	var beforeMasked = before != null ? MaskSensitive(before, AssertNotNull(beforeSensitive)) : null;
	var afterMasked = after != null ? MaskSensitive(after, AssertNotNull(afterSensitive)) : null;
	return DiffMask(type, address, before, after, beforeMasked, afterMasked);
}
static T? GetValueOrDefault<T>(T[] input, int index) => index < input.Length ? input[index] : default(T);
static T? GetValueOrDefaultS<T>(T[] input, int index) where T : struct => index < input.Length ? input[index] : default(T);
static IEnumerable<DiffItem> DiffMask(string type, string address, JsonNode? before, JsonNode? after, JsonNode? beforeMasked, JsonNode? afterMasked) {
	if (before == null || after == null || before.GetValueKind() != after.GetValueKind()) {
		if (before == null) {
			if (after == null) {
				// Actually no diff
			} else {
				yield return new DiffItem(address, "NULL", (string)AssertNotNull(after.ToString()));
			}
		} else if (after == null) {
			yield return new DiffItem(address, (string)AssertNotNull(before.ToString()), "NULL");
		} else {
			yield return new DiffItem(address, (string)before.GetValueKind().ToString(), (string)after.GetValueKind().ToString());
		}
		yield break;
	}
	AssertNotNull(beforeMasked);
	AssertNotNull(afterMasked);
	if (type == "helm_release" && address == ".set") {
		static string getName((JsonNode?,int) i) => AssertNotNull(FindChild(AssertNotNull(i.Item1), "name")).AsValue().GetValue<string>();
		var beforeItemsD = before.AsArray().ToArray().Cast<JsonNode>().Select((i,j) => (i,j)).ToLookup(getName).ToDictionary(i => i.Key, i => i.ToArray());
		var afterItemsD = after.AsArray().ToArray().Cast<JsonNode>().Select((i,j) => (i,j)).ToLookup(getName).ToDictionary(i => i.Key, i => i.ToArray());
		var beforeMaskedItems = beforeMasked.AsArray().ToArray();
		var afterMaskedItems = afterMasked.AsArray().ToArray();
		var keys = beforeItemsD.Concat(afterItemsD).Select(i => i.Key).Distinct();
		foreach (var key in keys) {
			var beforeItems = (beforeItemsD.TryGetValue(key, out var a1) ? a1 : null) ?? new (JsonNode,int)[0];
			var afterItems = (afterItemsD.TryGetValue(key, out var a2) ? a2 : null) ?? new (JsonNode,int)[0];
			var count = Math.Max(beforeItems.Length, afterItems.Length);
			for (var i = 0; i < count; i++) {
				var beforeI = GetValueOrDefaultS(beforeItems, i);
				var afterI = GetValueOrDefaultS(afterItems, i);
				foreach (var j in DiffMask(type, $"{address}[{key}][{i}]", beforeI?.i, afterI?.i, beforeI != null ? GetValueOrDefault(beforeMaskedItems, beforeI.Value.j) : null, afterI != null ? GetValueOrDefault(afterMaskedItems, afterI.Value.j) : null)) { yield return j; }
			}
		}
	} else if (before is JsonArray beforeA) {
		var beforeItems = beforeA.ToArray();
		var afterItems = after.AsArray().ToArray();
		var beforeMaskedItems = beforeMasked.AsArray().ToArray();
		var afterMaskedItems = afterMasked.AsArray().ToArray();
		var count = Math.Max(beforeItems.Length, afterItems.Length);
		for (var i = 0; i < count; i++) {
			foreach (var j in DiffMask(type, $"{address}[{i + 1}]", GetValueOrDefault(beforeItems, i), GetValueOrDefault(afterItems, i), GetValueOrDefault(beforeMaskedItems, i), GetValueOrDefault(afterMaskedItems, i))) { yield return j; }
		}
	} else if (before is JsonObject beforeO ) {
		var beforeItems = beforeO.ToDictionary(i => i.Key, i => i.Value);
		var afterItems = after.AsObject().ToDictionary(i => i.Key, i => i.Value);
		var beforeMaskedItems = beforeMasked.AsObject().ToDictionary(i => i.Key, i => i.Value);;
		var afterMaskedItems = afterMasked.AsObject().ToDictionary(i => i.Key, i => i.Value);;
		var keys = beforeItems.Keys.Union(afterItems.Keys);
		foreach (var key in keys) {
			foreach (var j in DiffMask(type, $"{address}.{key}", beforeItems.GetValueOrDefault(key), afterItems.GetValueOrDefault(key), beforeMaskedItems.GetValueOrDefault(key), afterMaskedItems.GetValueOrDefault(key))) { yield return j; }
		}
	} else {
		var beforeStr = before.ToString();
		var afterStr = after.ToString();
		if (beforeStr != afterStr) {
			yield return new DiffItem(address, beforeStr, afterStr);
		}
	}
}

internal record struct DiffItem(string address, string before, string after) { }

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(Policy))]
[JsonSerializable(typeof(string))]
internal partial class SourceGenerationContext : JsonSerializerContext { }

public class Policy {
	public PolicyElementUpdate[]? permittedDrifts;
	public PolicyElement[]? permittedDriftDeletes;
	public PolicyElement[]? permittedDeletes;
	public PolicyElement[]? permittedCreates;
	public PolicyElementUpdate[]? permittedUpdates;
}

public class PolicyElement {
	public required string addressRegex;
	public required string typeRegex;
}

public class PolicyElementUpdate : PolicyElement {
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
