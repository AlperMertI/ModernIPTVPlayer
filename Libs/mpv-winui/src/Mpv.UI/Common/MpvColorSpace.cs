namespace MpvWinUI.Common;

/// <summary>
/// Modern and clean wrapper for ColorSpaceType to avoid obsolete warnings in XAML generated code.
/// </summary>
public enum MpvColorSpace
{
    RgbFullG22NoneP709 = 0,
    RgbFullG10NoneP709 = 1,
    RgbStudioG22NoneP709 = 2,
    RgbStudioG22NoneP2020 = 3,
    Reserved = 4,
    YcbcrFullG22NoneP709X601 = 5,
    YcbcrStudioG22LeftP601 = 6,
    YcbcrFullG22LeftP601 = 7,
    YcbcrStudioG22LeftP709 = 8,
    YcbcrFullG22LeftP709 = 9,
    YcbcrStudioG22LeftP2020 = 10,
    YcbcrFullG22LeftP2020 = 11,
    RgbFullG2084NoneP2020 = 12,
    YcbcrStudioG2084LeftP2020 = 13,
    RgbStudioG2084NoneP2020 = 14,
    YcbcrStudioG22TopleftP2020 = 15,
    YcbcrStudioG2084TopleftP2020 = 16,
    RgbFullG22NoneP2020 = 17,
    YcbcrStudioGhlgTopleftP2020 = 18,
    YcbcrFullGhlgTopleftP2020 = 19,
    RgbStudioG24NoneP709 = 20,
    RgbStudioG24NoneP2020 = 21,
    YcbcrStudioG24LeftP709 = 22,
    YcbcrStudioG24LeftP2020 = 23,
    YcbcrStudioG24TopleftP2020 = 24
}
