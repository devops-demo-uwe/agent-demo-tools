# Azure Foundry Agents C# Function Calling Demo

This repository contains a small, heavily commented C# 10 console app that demonstrates the core idea behind tool calling with an Azure Foundry agent:

1. The app registers a local C# function as a tool definition.
2. The user asks a question that requires data the model does not already know.
3. The model requests a tool call by returning the function name and JSON arguments.
4. The C# app validates that request, runs the real local function, and sends the result back.
5. The model uses the returned data to write the final answer.

The important teaching point is that the LLM does not directly execute C# code. The model asks for a function call. Your application remains in control of what code runs, what arguments are accepted, and what output is returned.

## What this demo shows

The demo uses one mock function:

```csharp
GetWorkshopStatus(string workshopCode)
```

The function returns mock registration data for an internal workshop named `TOOLCALL-101`. The agent receives a user question like this:

```text
For workshop TOOLCALL-101, how many seats are left and should we open another room?
```

The model should recognize that workshop registration data is external application data, request the `getWorkshopStatus` tool, and then use the C# function result in its final response.

## Project files

| File | Purpose |
| --- | --- |
| `Program.cs` | The full console app and function-calling loop. |
| `AgentToolCallingDemo.csproj` | The .NET project file with C# 10 enabled and NuGet package references. |
| `appsettings.example.json` | Copy this to `appsettings.json` for local configuration. |
| `.github/copilot-instructions.md` | Repository instructions for GitHub Copilot or compatible coding agents. |
| `.gitignore` | Keeps build output and local secrets out of source control. |

## Prerequisites

You need the following before running the demo against Azure:

- .NET SDK installed.
- An Azure subscription with access to Azure AI Foundry.
- An Azure AI Foundry project URL.
- A deployed model in that Foundry project.
- Azure CLI login for local development, or managed identity when hosted in Azure.

For a typical local developer setup, authenticate with:

```powershell
az login
```

`DefaultAzureCredential` can use that Azure CLI sign-in when the console app starts. In Azure hosting, assign a managed identity to the app and grant it access to the Foundry project.

## Configuration

The app reads two settings:

| Setting | Meaning |
| --- | --- |
| `AZURE_AI_FOUNDRY_PROJECT_URL` | The Azure AI Foundry project URL used to create the agent. |
| `MODEL_DEPLOYMENT_NAME` | The deployment name of the model the agent should use. |

The project URL usually looks like this:

```text
https://<your-resource>.services.ai.azure.com/api/projects/<your-project>
```

Use .NET user secrets for local development so the Foundry project URL is stored outside source control. Environment variables are also supported, and `PROJECT_ENDPOINT` is accepted as a backwards-compatible fallback.

### Option 1: .NET user secrets

From the project folder, store the named local secrets:

```powershell
dotnet user-secrets set "AZURE_AI_FOUNDRY_PROJECT_URL" "https://<your-resource>.services.ai.azure.com/api/projects/<your-project>"
dotnet user-secrets set "MODEL_DEPLOYMENT_NAME" "<your-model-deployment-name>"
```

The project already has a `UserSecretsId`, so you do not need to run `dotnet user-secrets init`.

### Option 2: Environment variables

In PowerShell:

```powershell
$env:AZURE_AI_FOUNDRY_PROJECT_URL = "https://<your-resource>.services.ai.azure.com/api/projects/<your-project>"
$env:MODEL_DEPLOYMENT_NAME = "<your-model-deployment-name>"
```

### Option 3: appsettings.json

Copy the example file:

```powershell
Copy-Item appsettings.example.json appsettings.json
```

Edit `appsettings.json` so it contains your real values:

```json
{
  "AZURE_AI_FOUNDRY_PROJECT_URL": "https://<your-resource>.services.ai.azure.com/api/projects/<your-project>",
  "MODEL_DEPLOYMENT_NAME": "<your-model-deployment-name>"
}
```

`appsettings.json` is ignored by Git, but user secrets are preferred for local project details.

## Build and run

Build the project:

```powershell
dotnet build
```

Run the demo:

```powershell
dotnet run
```

If configuration is missing, the app prints a friendly message and exits before it calls Azure.

## Expected console flow

Exact wording from the model will vary, but the console flow should look roughly like this:

```text
Azure Foundry Agents - C# function tool demo
------------------------------------------------
Created agent: <agent-id>
Created thread: <thread-id>
Started run: <run-id>
The model requested function: getWorkshopStatus
Arguments: {"workshopCode":"TOOLCALL-101"}
C# function returned:
{
  "workshopCode": "TOOLCALL-101",
  "title": "Tool Calling with Azure Foundry Agents",
  "seatsRemaining": 3,
  "waitlistCount": 8,
  "recommendation": "Open another room or add a second session."
}
Run finished with status: Completed

[User] For workshop TOOLCALL-101, how many seats are left and should we open another room?
[Agent] The C# function returned that TOOLCALL-101 has 3 seats remaining and 8 people on the waitlist. You should open another room or add a second session.
Deleted thread: <thread-id>
Deleted agent: <agent-id>
```

## How the tool-calling loop works

The core loop is in `Program.cs`.

First, the app defines a tool contract:

```csharp
FunctionToolDefinition workshopStatusTool = new(
    name: "getWorkshopStatus",
    description: "Gets current registration status for an internal teaching workshop.",
    parameters: BinaryData.FromObjectAsJson(...));
```

This contract is what the model sees. It includes the tool name, a description, and a JSON schema for arguments. It does not expose the actual C# implementation.

Next, the app creates the agent with that tool definition:

```csharp
agent = client.Administration.CreateAgent(
    model: modelDeploymentName,
    name: "tool-calling-teaching-demo",
    instructions: "...",
    tools: new[] { workshopStatusTool });
```

Then the app starts a run and polls for status changes:

```csharp
run = client.Runs.GetRun(thread.Id, run.Id);
```

When the run enters `RunStatus.RequiresAction`, the model is asking the app to call a tool:

```csharp
if (run.Status == RunStatus.RequiresAction
    && run.RequiredAction is SubmitToolOutputsAction submitToolOutputsAction)
{
    ...
}
```

The app then inspects each requested tool call, validates the function name and arguments, and calls the real C# method:

```csharp
ToolOutput output = ResolveToolOutput(toolCall, workshopStatusTool.Name);
```

Finally, the app submits the output back to the run:

```csharp
run = client.Runs.SubmitToolOutputsToRun(run, toolOutputs, cancellationToken: default);
```

The model receives that tool output and can continue until it produces a final natural-language response.

## Why the app validates the tool call

Treat tool-call arguments as untrusted input. The model can produce malformed JSON, unknown function names, or missing properties. This demo keeps validation simple but explicit:

- It checks that the requested call is a function tool call.
- It checks that the function name is the expected function.
- It checks that `workshopCode` exists before calling the C# function.
- It returns JSON error messages instead of throwing for common bad requests.

For production code, also consider authorization, audit logging, rate limits, stricter schemas, timeouts, and human approval for side effects.

## Teaching script

Use this sequence for a short live explanation:

1. Show the user prompt in `Program.cs`.
2. Show `FunctionToolDefinition` and explain that it is the model-visible contract.
3. Show `GetWorkshopStatus` and explain that it is ordinary C# code.
4. Run the app.
5. Point out `The model requested function: getWorkshopStatus` in the console.
6. Point out the JSON arguments the model supplied.
7. Point out the C# function result.
8. Point out the final agent message that uses the function output.

A useful phrase for learners:

```text
The LLM chooses when a tool would help, but the host application executes the tool.
```

## Changing the demo function

To adapt the demo for another teaching scenario:

1. Rename the function in `GetWorkshopStatus` or add a new function.
2. Update the `FunctionToolDefinition` name, description, and JSON schema.
3. Update `ResolveToolOutput` so it parses the expected arguments.
4. Update the user prompt so the model has a reason to call the function.
5. Run `dotnet build`.
6. Run `dotnet run` and confirm the console shows the function request.

Keep tool descriptions specific. A vague description makes it harder for the model to decide when to call the tool.

## Notes about SDK choice

This demo uses `Azure.AI.Agents.Persistent` because its required-action loop makes the tool-calling mechanics very visible for teaching. Newer Azure Foundry examples may also use the Responses API, Azure AI Projects packages, or the Microsoft Agent Framework. Those abstractions can be useful for production apps, but they may hide some of the step-by-step mechanics this demo is intended to teach.

## Troubleshooting

### The app says configuration is missing

Set `AZURE_AI_FOUNDRY_PROJECT_URL` and `MODEL_DEPLOYMENT_NAME` with `dotnet user-secrets` or as environment variables.

### Authentication fails

Run:

```powershell
az login
```

Then rerun the demo. The app uses `DefaultAzureCredential`, which can authenticate with your Azure CLI identity locally and managed identity when hosted in Azure.

Then run the app again. Also confirm your signed-in identity has access to the Azure AI Foundry project.

### The run never reaches the function call

Check the agent instructions, the tool description, and the user prompt. The model needs enough context to know that the function is relevant.

### The model calls the function with odd arguments

Improve the JSON schema descriptions and keep validation in `ResolveToolOutput`. Tool arguments should always be checked before calling local code.

### The run expires

Runs have a limited lifetime. Keep local functions fast, use reasonable timeouts for external calls, and submit tool outputs promptly.

## Security reminders

- Do not return secrets in tool output.
- Do not trust tool-call arguments without validation.
- Avoid side effects in demo tools.
- Require explicit user confirmation for tools that modify data.
- Use least-privilege Azure identities.
- Keep local `appsettings.json` out of source control.

## Cleanup behavior

The demo deletes the thread and agent in a `finally` block. This keeps repeated teaching runs from leaving extra resources in the Foundry project. If you want to inspect the agent or thread in Foundry after a run, temporarily comment out the cleanup block while teaching, then restore it afterward.