namespace GenImageReviewer.Reviewers;

public record ReviewScore(
    int AccuracyScore,
    int QualityScore,
    List<string> Defects,
    string Commentary,
    int OverallScore,
    int? ReferenceAccuracyScore = null
);
