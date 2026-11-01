using LinuxWebTool.Application.Features.Security.Policies;
using LinuxWebTool.Contracts.Features.Security.Contracts;
using LinuxWebTool.WebHost.Composition;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LinuxWebTool.WebHost.Features.Security.Routes;

/// <summary>统一安全策略模板与沙箱审计控制器</summary>
[ApiController]
[Route("api/[controller]")]
[Authorize]
public class SecurityPoliciesController(
    ISecurityPolicyTemplateStore store,
    ISecurityPolicyEngine engine,
    IOperationLogger operationLogger) : MinimalApi.ControllerBase
{
    [HttpGet("Templates")]
    public async Task<IResult> GetTemplates()
    {
        var templates = await store.GetAllAsync();
        return Ok(templates);
    }

    [HttpPost("Templates")]
    public async Task<IResult> CreateTemplate([FromBody] CreateSecurityPolicyTemplateRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return BadRequest(new MessageResponse("策略模板名称不能为空"));
        }

        try
        {
            var createdId = await store.CreateAsync(request);
            var created = await store.GetByIdAsync(createdId);
            await operationLogger.LogAsync("新建安全策略模板", "安全凭据", request.Name, $"ID={createdId}", clientIp: HttpContext.GetClientIp());
            return Ok(created);
        }
        catch (Exception ex)
        {
            return BadRequest(new MessageResponse($"创建策略模板失败: {ex.Message}"));
        }
    }

    [HttpPut("Templates/{id}")]
    public async Task<IResult> UpdateTemplate(string id, [FromBody] UpdateSecurityPolicyTemplateRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return BadRequest(new MessageResponse("策略模板名称不能为空"));
        }

        try
        {
            var success = await store.UpdateAsync(id, request);
            if (!success)
            {
                return NotFound(new MessageResponse("策略模板不存在或无法修改"));
            }

            var updated = await store.GetByIdAsync(id);
            await operationLogger.LogAsync("修改安全策略模板", "安全凭据", request.Name, $"ID={id}", clientIp: HttpContext.GetClientIp());
            return Ok(updated);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new MessageResponse(ex.Message));
        }
        catch (Exception ex)
        {
            return BadRequest(new MessageResponse($"更新策略模板失败: {ex.Message}"));
        }
    }

    [HttpDelete("Templates/{id}")]
    public async Task<IResult> DeleteTemplate(string id)
    {
        var (success, error) = await store.DeleteAsync(id);
        if (!success)
        {
            return BadRequest(new MessageResponse(error ?? "删除策略模板失败"));
        }

        await operationLogger.LogAsync("删除安全策略模板", "安全凭据", id, "已删除", clientIp: HttpContext.GetClientIp());
        return Ok(new MessageResponse("策略模板删除成功"));
    }

    [HttpPost("Simulate")]
    public IResult Simulate([FromBody] SimulatePolicyRequest request)
    {
        var rules = System.Text.Json.JsonSerializer.Deserialize(request.RulesJson, LinuxWebTool.Infrastructure.Features.Security.Policies.SecurityJsonContext.Default.ListSecurityPolicyRule)
            ?? (IReadOnlyList<SecurityPolicyRule>)[];

        var evalCtx = new SecurityEvaluationContext(
            ApiKeyId: "simulation",
            ApiKeyName: "Dry-Run Simulator",
            Domain: request.Domain,
            ActionVerb: request.ActionVerb ?? ((request.Domain & SecurityTargetDomain.AllFiles) != 0 ? "Read" : "Execute"),
            TargetResource: "simulation",
            RawCommandOrPath: request.TestPayload,
            ClientIp: HttpContext.GetClientIp());

        var decision = engine.Simulate(rules, evalCtx);
        return Ok(new SimulatePolicyResponse(
            IsAllowed: decision.IsAllowed,
            RuleTriggered: decision.RuleName,
            DeniedReason: decision.DeniedReason,
            Suggestion: decision.Suggestion
        ));
    }

    [HttpGet("Capabilities")]
    public IResult GetCapabilities()
    {
        var capabilities = ModuleCapabilityRegistry.GetAll();
        return Ok(capabilities);
    }
}
