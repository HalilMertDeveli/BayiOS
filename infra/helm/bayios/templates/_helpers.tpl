{{/* Names */}}
{{- define "bayios.name" -}}
{{- default .Chart.Name .Values.nameOverride | trunc 63 | trimSuffix "-" -}}
{{- end -}}

{{- define "bayios.fullname" -}}
{{- if .Values.fullnameOverride -}}
{{- .Values.fullnameOverride | trunc 63 | trimSuffix "-" -}}
{{- else if contains (include "bayios.name" .) .Release.Name -}}
{{- .Release.Name | trunc 63 | trimSuffix "-" -}}
{{- else -}}
{{- printf "%s-%s" .Release.Name (include "bayios.name" .) | trunc 63 | trimSuffix "-" -}}
{{- end -}}
{{- end -}}

{{/* Common labels; call with (dict "root" $ "component" "api") */}}
{{- define "bayios.labels" -}}
helm.sh/chart: {{ printf "%s-%s" .root.Chart.Name .root.Chart.Version | replace "+" "_" }}
app.kubernetes.io/managed-by: {{ .root.Release.Service }}
app.kubernetes.io/version: {{ .root.Chart.AppVersion | quote }}
{{ include "bayios.selectorLabels" . }}
{{- end -}}

{{- define "bayios.selectorLabels" -}}
app.kubernetes.io/name: {{ include "bayios.name" .root }}
app.kubernetes.io/instance: {{ .root.Release.Name }}
app.kubernetes.io/component: {{ .component }}
{{- end -}}

{{/* "repo:tag", tag defaulting to appVersion; call with (dict "root" $ "image" .Values.api.image) */}}
{{- define "bayios.image" -}}
{{- printf "%s:%s" .image.repository (default .root.Chart.AppVersion .image.tag) -}}
{{- end -}}

{{- define "bayios.postgres.host" -}}
{{- printf "%s-postgres" (include "bayios.fullname" .) -}}
{{- end -}}

{{/* Secret with the in-cluster DB passwords (postgres.enabled only). */}}
{{- define "bayios.postgres.secretName" -}}
{{- printf "%s-postgres" (include "bayios.fullname" .) -}}
{{- end -}}

{{/* Secret that holds ConnectionStrings__Default for the API. */}}
{{- define "bayios.api.dbSecretName" -}}
{{- if .Values.postgres.enabled -}}
{{- printf "%s-api-db" (include "bayios.fullname" .) -}}
{{- else -}}
{{- required "api.database.existingSecret is required when postgres.enabled=false" .Values.api.database.existingSecret -}}
{{- end -}}
{{- end -}}

{{/* Restricted pod security defaults shared by every workload. */}}
{{- define "bayios.containerSecurityContext" -}}
allowPrivilegeEscalation: false
readOnlyRootFilesystem: true
capabilities:
  drop: ["ALL"]
{{- end -}}

{{/* HTTP probe; call with (dict "probe" .Values.api.probes.liveness "port" "http") */}}
{{- define "bayios.httpProbe" -}}
httpGet:
  path: {{ .probe.path }}
  port: {{ .port }}
periodSeconds: {{ .probe.periodSeconds }}
timeoutSeconds: {{ .probe.timeoutSeconds }}
failureThreshold: {{ .probe.failureThreshold }}
{{- end -}}
