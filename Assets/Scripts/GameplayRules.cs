public static class GameplayRules
{
    public const float WalkSpeed = 5f;
    public const float ForwardSpeedMultiplier = 4f;
    public const float JumpForce = 5f;

    public const float GlideStartSpeed = 9f;
    public const float MinGlideSpeed = 6f;
    public const float LevelGlideSpeed = 45f;
    public const float LevelGlideAcceleration = 25f;
    public const float DiveSpeedBonus = 8f;
    public const float GlideMaxSpeed = 28f;
    public const float GlideDiveAcceleration = 18f;
    public const float GlideClimbSlowdown = 55f;
    public const float GlideDrag = 0.08f;
    public const float GlideSink = 2.5f;
    public const float GlideMinDescentSpeed = 2f;
    public const float GlideTurnSpeed = 6f;

    public const float CheckpointBoostMultiplier = 2f;
    public const float CheckpointBoostDuration = 2f;
    public const float CheckpointGlideBoostBurst = 8f;

    public const float WindLiftHeight = 40f;
    public const float WindLiftAcceleration = 160f;
    public const float WindInstantUpwardSpeed = 35f;
    public const float WindMaxUpwardSpeed = 95f;
    public const float WindFastPlayerPredictionTime = 0.35f;
    public const float WindExtraHorizontalPadding = 4f;

    public const float ServerPositionPadding = 2f;
    public const float ServerMaxSingleUpdateDistance = 80f;

    public static float MaxOfficialHorizontalSpeed =>
        (LevelGlideSpeed + DiveSpeedBonus) * CheckpointBoostMultiplier;

    public static float MaxOfficialUpwardSpeed =>
        WindMaxUpwardSpeed;
}
