variable "aws_region" {
  description = "AWS region for all resources except CloudFront/ACM globals"
  type        = string
  default     = "eu-north-1"
}

variable "account_id" {
  description = "AWS account ID"
  type        = string
  default     = "716128130111"
}

variable "alert_emails" {
  description = "Email addresses subscribed to CloudWatch alerts and Budget notifications. In CI set via the TF_VAR_alert_emails env var (GitHub Actions variable ALERT_EMAILS); locally via a gitignored terraform.tfvars. Value must be HCL/JSON list syntax, e.g. [\"you@example.com\"]."
  type        = list(string)
  default     = []
}

variable "weekly_reports_enabled" {
  description = "Enable weekly report emails after configuring a verified SES sender"
  type        = bool
  default     = false
}
