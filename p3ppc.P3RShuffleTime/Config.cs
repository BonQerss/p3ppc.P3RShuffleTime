using p3ppc.P3RShuffleTime.Template.Configuration;
using System.ComponentModel;

namespace p3ppc.P3RShuffleTime.Configuration;

public class Config : Configurable<Config>
{
    [Category("General")]
    [DisplayName("Debug Logging")]
    [Description("Writes detailed reward and Shuffle Time diagnostics to the Reloaded II log.")]
    [DefaultValue(false)]
    public bool DebugEnabled { get; set; } = false;

    [Category("Shuffle Time Selection")]
    [DisplayName("Enable Selectable Cards")]
    [Description("Makes Shuffle Time cards immediately selectable without the normal flip, shuffle, or countdown sequence.")]
    [DefaultValue(true)]
    public bool EnableSelectableCards { get; set; } = true;

    [Category("Shuffle Time Selection")]
    [DisplayName("Allow Look Away (Cancel)")]
    [Description("With selectable cards, pressing cancel while picking opens the game's own look away prompt. Yes ends Shuffle Time with no card, No returns to the pick.")]
    [DefaultValue(true)]
    public bool EnableLookAway { get; set; } = true;

    [Category("Card Preview")]
    [DisplayName("Card Reward Preview")]
    [Description("What a highlighted Sword or Coin card shows while picking. ShuffleMessage shows the card's line from the Shuffle Time messages, ItemDescription shows the Skill Card's own description, Both shows the message with the description under it.")]
    [DefaultValue(PreviewTextMode.ShuffleMessage)]
    public PreviewTextMode PreviewText { get; set; } = PreviewTextMode.ShuffleMessage;

    [Category("Shuffle Time Cards")]
    [DisplayName("Disable Blank Card Generation")]
    [Description("Prevents blank cards from being generated. Takes priority over regenerating blanks.")]
    [DefaultValue(true)]
    public bool DisableBlankCardGeneration { get; set; } = true;

    [Category("Shuffle Time Cards")]
    [DisplayName("Regenerate Blank Cards")]
    [Description("When blank cards are allowed, generates a normal reward in each blank spot.")]
    [DefaultValue(false)]
    public bool RegenerateBlankCards { get; set; } = false;

    [Category("Sword Cards")]
    [DisplayName("Sword Cards Give Skill Cards")]
    [Description("Replaces Sword card weapon rewards with random Skill Cards from the configured rank pools.")]
    [DefaultValue(true)]
    public bool EnableSwordSkillCards { get; set; } = true;

    [Category("Duplicate Persona Cards")]
    [DisplayName("Enable Arcana EXP")]
    [Description("When a duplicate Persona is selected, gives owned Personas of the same Arcana 15 to 25 percent of their remaining EXP to the next level.")]
    [DefaultValue(true)]
    public bool EnableDuplicatePersonaArcanaExp { get; set; } = true;

    [Category("Duplicate Persona Cards")]
    [DisplayName("Include Duplicate Persona")]
    [Description("Includes the Persona shown on the duplicate card in the Arcana EXP reward.")]
    [DefaultValue(true)]
    public bool IncludeSelectedPersona { get; set; } = true;
}

public enum PreviewTextMode
{
    ShuffleMessage,
    ItemDescription,
    Both,
    Off
}

public class ConfiguratorMixin : ConfiguratorMixinBase
{
}
