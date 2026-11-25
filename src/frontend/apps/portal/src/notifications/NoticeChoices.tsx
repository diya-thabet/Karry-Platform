import { useMutation, useQueryClient } from "@tanstack/react-query";
import { useTranslation } from "@karry/i18n";
import { Card, Checkbox, ErrorState, useToast } from "@karry/ui";
import { useApi } from "../app/api";
import { noticePreferencesKey, noticeTypeName, useNoticePreferences, type NoticePreference } from "./api";

/**
 * How the person is told (ADR-0039 §2): for each kind of notice, the channels they may turn off for themselves (the
 * daily e-mail digest, push). The list in the portal and on the tablet is always on: it is where every notice is
 * kept. A kind the company switched off is shown as such, its choices kept for when it is on again.
 */
export function NoticeChoices() {
  const { t } = useTranslation();
  const preferences = useNoticePreferences();
  const choosable = (preferences.data ?? []).filter((preference) => Object.keys(preference.channels).length > 0);

  if (preferences.isError) {
    return <ErrorState onRetry={() => void preferences.refetch()} />;
  }

  if (choosable.length === 0) {
    return null;
  }

  return (
    <Card>
      <section className="flex flex-col gap-4" aria-labelledby="notice-choices">
        <div className="flex flex-col gap-1">
          <h2 id="notice-choices" className="text-lg font-semibold">{t("portal.notices.choices.title")}</h2>
          <p className="text-sm text-text-muted">{t("portal.notices.choices.description")}</p>
        </div>
        <ul className="flex flex-col divide-y divide-divider">
          {choosable.map((preference) => <Choice key={preference.type} preference={preference} />)}
        </ul>
      </section>
    </Card>
  );
}

function Choice({ preference }: { preference: NoticePreference }) {
  const { t } = useTranslation();
  const api = useApi();
  const queryClient = useQueryClient();
  const toast = useToast();
  const name = noticeTypeName(t, preference.type);

  const set = useMutation({
    mutationFn: async ({ channel, enabled }: { channel: string; enabled: boolean }) =>
      (await api.PUT("/api/v1/notifications/preferences/{noticeType}/{channel}", {
        params: { path: { noticeType: preference.type, channel } },
        body: { enabled },
      })).data,
    onSuccess: (saved) => {
      if (saved) {
        queryClient.setQueryData(noticePreferencesKey, saved);
      }

      toast({ status: "ok", title: t("portal.notices.choices.saved") });
    },
  });

  return (
    <li className="flex flex-col gap-2 py-3">
      <span className="font-semibold">{name}</span>
      {!preference.ruleEnabled && <span className="text-sm text-text-muted">{t("portal.notices.choices.ruleOff")}</span>}
      <div className="flex flex-wrap gap-x-6 gap-y-2">
        {Object.entries(preference.channels).map(([channel, enabled]) => (
          <Checkbox
            key={channel}
            label={t(`portal.notices.channel.${channel}`, { defaultValue: channel })}
            aria-label={`${name}: ${t(`portal.notices.channel.${channel}`, { defaultValue: channel })}`}
            checked={enabled}
            disabled={set.isPending}
            onChange={(event) => { set.mutate({ channel, enabled: event.currentTarget.checked }); }}
          />
        ))}
      </div>
    </li>
  );
}
