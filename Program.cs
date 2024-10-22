using System.Text.Json;
using System.Text.RegularExpressions;

if(args.Length != 2 || args.Length > 0 && args[0] == "--help" || args[0] == "-h") {
	Console.Write("Expected arguments: [terraformPlanFileName] [terraformPolicyFileName]");
	return 2;
}
string terraformPlanFileName = args[0];
string terraformPolicyFileName = args[1];
var planStream = new FileStream(terraformPlanFileName, FileMode.Open, FileAccess.Read);
var policyStream = new FileStream(terraformPolicyFileName, FileMode.Open, FileAccess.Read);
var policy = JsonSerializer.Deserialize<Policy>(policyStream, new JsonSerializerOptions { AllowTrailingCommas = true, ReadCommentHandling = JsonCommentHandling.Skip, IncludeFields = true });
//Console.WriteLine(J(policy));

var document = System.Text.Json.JsonDocument.Parse(planStream, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
bool failed = false;


foreach(var drift in findChild(document.RootElement, "resource_drift").EnumerateArray().ToArray()) {
	var address = drift.GetProperty("address").GetString();
	var type = drift.GetProperty("type").GetString();
	var change = findChild(drift, "change");
	var before = findChild(change, "before");
	var after = findChild(change, "after");
	var actions = getStringArray(findChild(change, "actions"));
	if (!Enumerable.SequenceEqual(actions, new[] { "update" })) {
		Console.WriteLine($"Drift unknown action: {String.Join(", ", actions)}");
		failed = true;
		continue;
	}

	var diffs = diffI(type, "", before, after);
	var policies = getPolicyElements(policy.permittedDrifts, address, type).SelectMany(i => i.permittedUpdates ?? new PolicyItem[0]).ToArray();
	//Console.WriteLine(J(policies));
	Console.WriteLine($"Drift diff: {address} - {type}");
	foreach(var diff in diffs) {
		Console.WriteLine($"Drift diff: {diff.address} - {diff.before} != {diff.after}");
		var allowed = policies.Any(i => Regex.IsMatch(diff.address, makeAllStringMatch(i.addressRegex)) && (i.beforeRegex == null || Regex.IsMatch(diff.before, makeAllStringMatch(i.beforeRegex))) && (i.afterRegex == null || Regex.IsMatch(diff.after, makeAllStringMatch(i.afterRegex))));

		if (allowed) {
			Console.WriteLine($"   Allowed");
		} else {
			Console.WriteLine($"   DENIED");
			failed = true;
		}
	}
}

foreach(var changeI in findChild(document.RootElement, "resource_changes").EnumerateArray().ToArray()) {
	var address = changeI.GetProperty("address").GetString()!;
	var type = changeI.GetProperty("type").GetString()!;
	var change = findChild(changeI, "change");
	var before = findChild(change, "before");
	var after = findChild(change, "after");
	var actions = getStringArray(findChild(change, "actions"));

	if (Enumerable.SequenceEqual(actions, new[] { "no-op" }) || Enumerable.SequenceEqual(actions, new[] { "read" })) {
		continue;
	}
	if (actions.Contains("update")) {
		Console.WriteLine($"Update: {address} - {type}");
		var policies = getPolicyElements(policy.permittedUpdates, address, type).ToArray();
		if (!policies.Any()) {
			Console.WriteLine($"     DENIED");
			failed = true;
		} else {
			var diffs = diffI(type, "", before, after);
			var itemPolicies = policies.SelectMany(i => i.permittedUpdates ?? new PolicyItem[0]);
			foreach(var diff in diffs) {
				Console.WriteLine($"Update diff: {diff.address} - {diff.before} != {diff.after}");
				var allowed = itemPolicies.Any(i => elementMatch(diff, i, before, after));

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
		var policies = getPolicyElements(policy.permittedCreates, address, type).ToArray();
		if (!policies.Any()) {
			Console.WriteLine($"     DENIED");
			failed = true;
		} else {
			Console.WriteLine($"     Allowed");
		}
	} else if (actions.Contains("delete")) {
		Console.WriteLine($"Delete: {address} - {type}");
		var policies = getPolicyElements(policy.permittedDeletes, address, type).ToArray();
		if (!policies.Any()) {
			Console.WriteLine($"     DENIED");
			failed = true;
		} else {
			Console.WriteLine($"     Allowed");
		}
	} else {
		Console.WriteLine($"Drift unknown action: {String.Join(", ", actions)}");
		failed = true;
	}
}

Console.WriteLine($"Final Outcome: {(failed?"DENIED":"Allowed")}");
return failed ? 1 : 0;

static bool elementMatch(DiffItem diffItem, PolicyItem policyItem, JsonElement beforeRoot, JsonElement afterRoot) {
	var matchA = Regex.Match(diffItem.address, makeAllStringMatch(policyItem.addressRegex));
	var match = matchA.Success && (policyItem.beforeRegex == null || Regex.IsMatch(diffItem.before, makeAllStringMatch(policyItem.beforeRegex))) && (policyItem.afterRegex == null || Regex.IsMatch(diffItem.after, makeAllStringMatch(policyItem.afterRegex)));
	if (match) {
		foreach(var whenItem in policyItem.when ?? new PolicyItemWhen[0]) {
			var address = Regex.Replace(whenItem.address, "\\$(\\d+)", (m) => {
				return matchA.Groups[int.Parse(m.Groups[1].Value)].Value;
			});
			var beforeElement = findElement(beforeRoot, address);
			var afterElement = findElement(afterRoot, address);
			match = (whenItem.beforeRegex == null || Regex.IsMatch(beforeElement.ToString()!, makeAllStringMatch(whenItem.beforeRegex))) && (whenItem.afterRegex == null || Regex.IsMatch(afterElement.ToString()!, makeAllStringMatch(whenItem.afterRegex)));
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
static JsonElement findElement(JsonElement root, string path) {
	if (path == "") return root;
	var nextIndex = path.IndexOfAny(new[] { '.', '[', ']' }, 1);
	var key = nextIndex == -1 ? path.Substring(1) : path.Substring(1, nextIndex - 1);
	if (path[0] == '.') {
		var remainder = nextIndex == -1 ? "" : path.Substring(nextIndex);
		return findElement(findChild(root, key), remainder);
	} else if (path[0] == '[') {
		var remainder = nextIndex == -1 ? "" : path.Substring(nextIndex+1);
		return findElement(findChildA(root, int.Parse(key)), remainder);
	} else {
		throw new Exception($"Unknown path: {path}");
	}
}
static PolicyElement[] getPolicyElements(PolicyElement[]? list, string address, string type) {
	if (list == null) return new PolicyElement[0];
	return list.Where(i => Regex.IsMatch(address, makeAllStringMatch(i.addressRegex)) && Regex.IsMatch(type, makeAllStringMatch(i.typeRegex))).ToArray();
}

static string makeAllStringMatch(string regex) => $"^(?:{regex})$";
static string J(object o) => JsonSerializer.Serialize(o, new JsonSerializerOptions { AllowTrailingCommas = true, ReadCommentHandling = JsonCommentHandling.Skip, IncludeFields = true, WriteIndented = true });
static JsonElement findChild(JsonElement el, string name) {
	var r = el.EnumerateObject().Cast<JsonProperty?>().FirstOrDefault(i => i!.Value.Name == name);
	if (r == null) {
		throw new Exception($"{name} not found in {el}");
	}
	return r.Value.Value;
};

static JsonElement findChildA(JsonElement el, int index) {
	var r = el.EnumerateArray().Skip(index).Cast<JsonElement?>().FirstOrDefault();
	if (r == null) {
		throw new Exception($"{index} not found in {el}");
	}
	return r.Value;
};
static string[] getStringArray(JsonElement el) => el.EnumerateArray().Select(i => i.GetString()!).ToArray();
//[System.Diagnostics.CodeAnalysis.DoesNotReturn]
//static void throwE(string label, string address) => throw new Exception($"{label} at {address}");

static IEnumerable<DiffItem> diffI(string type, string address, JsonElement? before, JsonElement? after) {
	if (before == null || after == null || before.Value.ValueKind != after.Value.ValueKind) {
		if (before == null) {
			yield return new DiffItem(address, "NULL", after.ToString());
		} else if (after == null) {
			yield return new DiffItem(address, before.ToString(), "NULL");
		} else {
			yield return new DiffItem(address, before.Value.ValueKind.ToString(), after.Value.ValueKind.ToString());
		}
		yield break;
	}
	var beforeV = before.Value;
	var afterV = after.Value;
	if (type == "helm_release" && address == ".set") {
		static string getName(JsonElement i) => findChild(i, "name").GetString()!;
		var beforeItemsD = beforeV.EnumerateArray().ToArray().ToLookup(getName).ToDictionary(i => i.Key, i => i.ToArray());
		var afterItemsD = afterV.EnumerateArray().ToArray().ToLookup(getName).ToDictionary(i => i.Key, i => i.ToArray());
		var keys = beforeItemsD.Concat(afterItemsD).Select(i => i.Key).Distinct();
		foreach(var key in keys) {
			var beforeItems = (beforeItemsD.TryGetValue(key, out var a1) ? a1 : null) ?? new JsonElement[0];
			var afterItems = (afterItemsD.TryGetValue(key, out var a2) ? a2 : null) ?? new JsonElement[0];
			var count = Math.Max(beforeItems.Length, afterItems.Length);
			for(var i = 0; i < count; i++) {
				foreach(var j in diffI(type, $"{address}[{key}][{i}]", i < beforeItems.Length ? beforeItems[i] : null,  i < afterItems.Length ? afterItems[i] : null)) { yield return j; }
			}
		}
	} else if (beforeV.ValueKind == JsonValueKind.Array) {
		var beforeItems = beforeV.EnumerateArray().ToArray();
		var afterItems = afterV.EnumerateArray().ToArray();
		var count = Math.Max(beforeItems.Length, afterItems.Length);
		for(var i = 0; i < count; i++) {
			foreach(var j in diffI(type, $"{address}[{i+1}]", i < beforeItems.Length ? beforeItems[i] : null,  i < afterItems.Length ? afterItems[i] : null)) { yield return j; }
		}
	} else if (beforeV.ValueKind == JsonValueKind.Object) {
		var beforeItems = beforeV.EnumerateObject().ToDictionary(i => i.Name, i => i.Value);
		var afterItems = afterV.EnumerateObject().ToDictionary(i => i.Name, i => i.Value);
		var keys = beforeItems.Keys.Union(afterItems.Keys);
		foreach(var key in keys) {
			foreach(var j in diffI(type, $"{address}.{key}", beforeItems.TryGetValue(key, out var bI) ? bI : null, afterItems.TryGetValue(key, out var aI) ? aI : null)) { yield return j; }
		}
	} else {
		var beforeStr = beforeV.ToString();
		var afterStr = afterV.ToString();
		if (beforeStr != afterStr) {
			yield return new DiffItem(address, beforeStr, afterStr);
		}
	}
}

record struct DiffItem(string address, string? before, string? after) {}

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
