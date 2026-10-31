namespace Chat.Data;

// CLR names are snake_cased into the PostgreSQL enum type names (user_role, user_status, ...),
// and member names into the labels (Fcm -> 'fcm').

public enum UserRole { User, Admin }

public enum UserStatus { Pending, Active, Banned }

public enum PushPlatform { Fcm, Apns, Web }

public enum VerificationPurpose { VerifyEmail, ResetPassword }

public enum ConversationType { Direct, Group }

public enum MemberRole { Owner, Admin, Member }

public enum MessageType { Text, Media, System }

public enum MediaStatus { Pending, Ready, Rejected }

public enum ReportReason { Spam, Harassment, Other }

public enum ReportStatus { Open, Actioned, Dismissed }
