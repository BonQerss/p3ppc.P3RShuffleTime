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

    [Category("Sword Cards")]
    [DisplayName("Sword Cards Give Skill Cards")]
    [Description("Replaces Sword card weapon rewards with random Skill Cards from the configured rank pools.")]
    [DefaultValue(true)]
    public bool EnableSwordSkillCards { get; set; } = true;

    [Category("Duplicate Persona Cards")]
    [DisplayName("Enable Arcana EXP")]
    [Description("When a duplicate Persona is selected, gives owned Personas of the same Arcana 15-25% of their remaining EXP to the next level.")]
    [DefaultValue(true)]
    public bool EnableDuplicatePersonaArcanaExp { get; set; } = true;

    [Category("Duplicate Persona Cards")]
    [DisplayName("Include Duplicate Persona")]
    [Description("Includes the Persona shown on the duplicate card in the Arcana EXP reward.")]
    [DefaultValue(true)]
    public bool IncludeSelectedPersona { get; set; } = true;

}

public class ConfiguratorMixin : ConfiguratorMixinBase
{
}
