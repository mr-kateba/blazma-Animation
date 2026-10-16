using Blazma.Rigging;
using Blazma.Rigging.Mapping;

namespace Blazma.Tests.Rigging.Mapping;

public class BoneNameNormalizerTests
{
    [Theory]
    [InlineData("mixamorig:LeftUpLeg", Side.Left, "upleg")]
    [InlineData("mixamorig1:RightForeArm", Side.Right, "forearm")]
    [InlineData("mixamorig:Hips", null, "hips")]
    [InlineData("Armature|LeftArm", Side.Left, "arm")]
    [InlineData("DEF-upper_arm.L", Side.Left, "upperarm")]
    [InlineData("DEF-thigh.R", Side.Right, "thigh")]
    [InlineData("ORG-spine.001", null, "spine1")]
    [InlineData("spine.003", null, "spine3")]
    [InlineData("Bip01 L Thigh", Side.Left, "thigh")]
    [InlineData("Bip01 R Toe0", Side.Right, "toe0")]
    [InlineData("Bip01 Spine1", null, "spine1")]
    [InlineData("Bip01", null, "bip01")]
    [InlineData("J_Bip_L_UpperArm", Side.Left, "upperarm")]
    [InlineData("J_Bip_C_Hips", null, "hips")]
    [InlineData("CC_Base_R_Calf", Side.Right, "calf")]
    [InlineData("upperarm_l", Side.Left, "upperarm")]
    [InlineData("clavicle_r", Side.Right, "clavicle")]
    [InlineData("neck_01", null, "neck1")]
    [InlineData("l_thigh", Side.Left, "thigh")]
    [InlineData("LeftToe_End", Side.Left, "toeend")]
    [InlineData("thighL", Side.Left, "thigh")]
    [InlineData("UpperArmR", Side.Right, "upperarm")]
    [InlineData("leftarm", Side.Left, "arm")]
    [InlineData("Spine", null, "spine")]
    public void Parse_extracts_side_and_base_name(string name, Side? side, string key)
    {
        var (s, k) = BoneNameNormalizer.Parse(name);
        Assert.Equal(side, s);
        Assert.Equal(key, k);
    }
}
