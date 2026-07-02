using Azure;
using Azure.AI.Agents.Persistent;
using Azure.Identity;
using Microsoft.Extensions.Configuration;
using System.Text.Json;

// This demo intentionally uses top-level statements so learners can read it from
// top to bottom like a script. The important idea is simple: the model can ask
// for a tool call, but this C# program decides whether and how to run the real
// local function.

Console.WriteLine("Azure Foundry Agents - C# function tool demo");
Console.WriteLine("------------------------------------------------");

const string FoundryProjectUrlSettingName = "AZURE_AI_FOUNDRY_PROJECT_URL";
const string ModelDeploymentNameSettingName = "MODEL_DEPLOYMENT_NAME";

// Configuration can come from appsettings.json, .NET user secrets, or environment variables.
// User secrets and environment variables keep local project details out of source control.
IConfigurationRoot configuration = new ConfigurationBuilder()
	.SetBasePath(AppContext.BaseDirectory)
	.AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
	.AddUserSecrets<Program>(optional: true)
	.AddEnvironmentVariables()
	.Build();

string? foundryProjectUrl = FirstConfiguredValue(
	configuration[FoundryProjectUrlSettingName],
	configuration["PROJECT_ENDPOINT"]);

string? modelDeploymentName = configuration[ModelDeploymentNameSettingName];

if (IsMissing(foundryProjectUrl) || IsMissing(modelDeploymentName))
{
	Console.WriteLine("Configuration is missing.");
	Console.WriteLine();
	Console.WriteLine($"Set {FoundryProjectUrlSettingName} and {ModelDeploymentNameSettingName}");
	Console.WriteLine("with dotnet user-secrets or environment variables, then run the app again.");
	Console.WriteLine();
	Console.WriteLine("Example Foundry project URL format:");
	Console.WriteLine("https://<your-resource>.services.ai.azure.com/api/projects/<your-project>");
	return;
}

if (!Uri.TryCreate(foundryProjectUrl, UriKind.Absolute, out Uri? foundryProjectUri)
	|| foundryProjectUri.Scheme != Uri.UriSchemeHttps)
{
	Console.WriteLine($"{FoundryProjectUrlSettingName} must be an absolute HTTPS URL.");
	Console.WriteLine("Example Foundry project URL format:");
	Console.WriteLine("https://<your-resource>.services.ai.azure.com/api/projects/<your-project>");
	return;
}

// DefaultAzureCredential tries several developer-friendly credential sources.
// For most local demos, run `az login` first and it will use your Azure CLI sign-in.
// In Azure hosting, the same credential can use a managed identity assigned to the app.
PersistentAgentsClient client = new(foundryProjectUri.AbsoluteUri, new DefaultAzureCredential());

// FunctionToolDefinition is the contract the model sees. It does not expose the
// C# method body; it only exposes the function name, description, and JSON schema
// for arguments. Clear descriptions matter because the model uses them to decide
// when a tool is useful.
FunctionToolDefinition workshopStatusTool = new(
	name: "getWorkshopStatus",
	description: "Gets current registration status for an internal teaching workshop.",
	parameters: BinaryData.FromObjectAsJson(
		new
		{
			Type = "object",
			Properties = new
			{
				WorkshopCode = new
				{
					Type = "string",
					Description = "The workshop code, for example TOOLCALL-101."
				}
			},
			Required = new[] { "workshopCode" }
		},
		new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));

PersistentAgent? agent = null;
PersistentAgentThread? thread = null;

try
{
	// The agent is created in Azure Foundry with the tool contract attached.
	// The local C# function is still only available inside this program.
	agent = client.Administration.CreateAgent(
		model: modelDeploymentName,
		name: "tool-calling-teaching-demo",
		instructions: "You help instructors explain tool calling. "
			+ "Use the getWorkshopStatus tool when a user asks about workshop seats, "
			+ "waitlists, or room recommendations. Keep answers concise and explain "
			+ "that the data came from the C# function.",
		tools: new[] { workshopStatusTool });

	Console.WriteLine($"Created agent: {agent.Id}");

	// A thread is the conversation container. Messages and runs happen inside it.
	thread = client.Threads.CreateThread();
	Console.WriteLine($"Created thread: {thread.Id}");

	// This prompt is designed to encourage the model to call the function because
	// the answer depends on live application data the model does not already know.
	client.Messages.CreateMessage(
		thread.Id,
		MessageRole.User,
		"For workshop TOOLCALL-101, how many seats are left and should we open another room?");

	ThreadRun run = client.Runs.CreateRun(thread.Id, agent.Id);
	Console.WriteLine($"Started run: {run.Id}");

	// Runs move through states such as queued, in_progress, requires_action, and
	// completed. requires_action is the interesting state for tool calling: the
	// model has asked the app to run one or more tools and return the outputs.
	do
	{
		Thread.Sleep(TimeSpan.FromMilliseconds(500));
		run = client.Runs.GetRun(thread.Id, run.Id);

		if (run.Status == RunStatus.RequiresAction
			&& run.RequiredAction is SubmitToolOutputsAction submitToolOutputsAction)
		{
			List<ToolOutput> toolOutputs = new();

			foreach (RequiredToolCall toolCall in submitToolOutputsAction.ToolCalls)
			{
				ToolOutput output = ResolveToolOutput(toolCall, workshopStatusTool.Name);
				toolOutputs.Add(output);
			}

			// This sends the local C# function result back to the model. The model
			// can then use the returned JSON to produce the final natural-language
			// answer for the user.
			run = client.Runs.SubmitToolOutputsToRun(run, toolOutputs, cancellationToken: default);
		}
	}
	while (run.Status == RunStatus.Queued
		|| run.Status == RunStatus.InProgress
		|| run.Status == RunStatus.RequiresAction);

	Console.WriteLine($"Run finished with status: {run.Status}");
	Console.WriteLine();

	Pageable<PersistentThreadMessage> messages = client.Messages.GetMessages(
		threadId: thread.Id,
		order: ListSortOrder.Ascending);

	foreach (PersistentThreadMessage message in messages)
	{
		foreach (MessageContent content in message.ContentItems)
		{
			if (content is MessageTextContent text)
			{
				Console.WriteLine($"[{message.Role}] {text.Text}");
			}
		}
	}
}
finally
{
	// Cleanup keeps repeated teaching runs tidy in the Foundry project.
	if (thread is not null)
	{
		client.Threads.DeleteThread(thread.Id);
		Console.WriteLine($"Deleted thread: {thread.Id}");
	}

	if (agent is not null)
	{
		client.Administration.DeleteAgent(agent.Id);
		Console.WriteLine($"Deleted agent: {agent.Id}");
	}
}

static ToolOutput ResolveToolOutput(RequiredToolCall toolCall, string expectedToolName)
{
	if (toolCall is not RequiredFunctionToolCall functionToolCall)
	{
		return new ToolOutput(toolCall, JsonSerializer.Serialize(new
		{
			error = "Unsupported tool call type."
		}));
	}

	Console.WriteLine($"The model requested function: {functionToolCall.Name}");
	Console.WriteLine($"Arguments: {functionToolCall.Arguments}");

	if (!string.Equals(functionToolCall.Name, expectedToolName, StringComparison.Ordinal))
	{
		return new ToolOutput(toolCall, JsonSerializer.Serialize(new
		{
			error = $"Unknown function '{functionToolCall.Name}'."
		}));
	}

	using JsonDocument arguments = JsonDocument.Parse(functionToolCall.Arguments);

	if (!arguments.RootElement.TryGetProperty("workshopCode", out JsonElement workshopCodeElement))
	{
		return new ToolOutput(toolCall, JsonSerializer.Serialize(new
		{
			error = "Missing required argument 'workshopCode'."
		}));
	}

	string workshopCode = workshopCodeElement.GetString() ?? string.Empty;
	string functionResult = GetWorkshopStatus(workshopCode);

	Console.WriteLine("C# function returned:");
	Console.WriteLine(functionResult);

	return new ToolOutput(toolCall, functionResult);
}

static string GetWorkshopStatus(string workshopCode)
{
	// This is the ordinary C# function the model can ask us to call. In a real app,
	// this might query a database, call an internal API, or calculate something the
	// model cannot know by itself. Here it returns mock data so the demo stays small.
	object result = workshopCode.Trim().ToUpperInvariant() switch
	{
		"TOOLCALL-101" => new
		{
			workshopCode = "TOOLCALL-101",
			title = "Tool Calling with Azure Foundry Agents",
			seatsRemaining = 3,
			waitlistCount = 8,
			recommendation = "Open another room or add a second session."
		},
		_ => new
		{
			workshopCode,
			error = "Workshop code was not found in the demo data."
		}
	};

	return JsonSerializer.Serialize(result, new JsonSerializerOptions
	{
		WriteIndented = true
	});
}

static string? FirstConfiguredValue(params string?[] values)
{
	foreach (string? value in values)
	{
		if (!IsMissing(value))
		{
			return value;
		}
	}

	return null;
}

static bool IsMissing(string? value)
{
	return string.IsNullOrWhiteSpace(value) || value.Contains("<", StringComparison.Ordinal);
}
