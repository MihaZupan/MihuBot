﻿namespace MihuBot;

public abstract class CooldownTrackable
{
    private readonly CooldownTracker _cooldownTracker;


    protected virtual int CooldownToleranceCount => 1;
    protected virtual TimeSpan Cooldown => TimeSpan.Zero;

    public CooldownTrackable()
    {
        var cooldown = Cooldown;

        _cooldownTracker = cooldown > TimeSpan.Zero
            ? new CooldownTracker(cooldown, CooldownToleranceCount, adminOverride: true)
            : CooldownTracker.NoCooldownTracker;
    }

    public bool TryPeek(MessageContext ctx) =>
        TryPeek(ctx.AuthorId);

    public bool TryPeek(ulong userId) =>
        _cooldownTracker.TryPeek(userId);

    public bool TryEnter(MessageContext ctx) =>
        TryEnter(ctx.AuthorId);

    public bool TryEnter(ulong userId) =>
        _cooldownTracker.TryEnter(userId);

    public bool TryEnter(MessageContext ctx, out TimeSpan cooldown, out bool shouldWarn) =>
        _cooldownTracker.TryEnter(ctx.AuthorId, out cooldown, out shouldWarn);

    public async Task<bool> TryEnterOrWarnAsync(MessageContext ctx)
    {
        if (TryEnter(ctx, out var cooldown, out bool shouldWarn))
            return true;

        if (shouldWarn)
        {
            await ctx.WarnCooldownAsync(cooldown);
        }

        return false;
    }
}
