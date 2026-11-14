import { useAuth } from '../../auth/AuthContext';
import { PageHeader, Tabs } from '../../components/ui';
import { useApi } from '../../api/hooks';
import type { DashboardSummaryDto } from '../../api/types';
import { useUrlState } from '../../lib/urlState';
import { ApprovalsTab } from './ApprovalsTab';
import { DeploymentCodesTab } from './DeploymentCodesTab';
import { PackagesTab } from './PackagesTab';
import { TokensTab } from './TokensTab';

export function AgentManagementPage() {
  const url = useUrlState();
  const { canOperate } = useAuth();
  const tab = url.get('tab', 'tokens');
  const summary = useApi<DashboardSummaryDto>('/dashboard/summary', undefined, { staleTime: 30_000 });
  return (
    <>
      <PageHeader
        title="Agent Management"
        subtitle={
          canOperate
            ? 'Deploy the EasyPatch agent with enrollment tokens (msiexec on Windows, easypatch-agent enroll on Linux) or tokenless deployment codes (RMM scripts with approval).'
            : 'Enrollment tokens, deployment codes, pending approvals and agent packages.'
        }
      />
      <Tabs
        active={tab}
        onChange={(t) => url.set({ tab: t === 'tokens' ? null : t, create: null, status: null })}
        tabs={[
          { id: 'tokens', label: 'Enrollment tokens' },
          { id: 'codes', label: 'Deployment codes' },
          { id: 'approvals', label: 'Pending approvals', count: summary.data?.pendingApprovals || null },
          { id: 'packages', label: 'Agent packages' },
        ]}
      />
      {tab === 'tokens' && <TokensTab />}
      {tab === 'codes' && <DeploymentCodesTab />}
      {tab === 'approvals' && <ApprovalsTab />}
      {tab === 'packages' && <PackagesTab />}
    </>
  );
}
