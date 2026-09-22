import { ref } from 'vue';
import { message } from 'ant-design-vue';
import { api } from '@/api/http';

export interface ModelOption {
  id: string;
  object?: string;
  owned_by?: string;
}

export function useModelList() {
  const models = ref<ModelOption[]>([]);
  const loading = ref(false);

  const fetchModels = async (baseUrl: string, apiKey = '') => {
    if (!baseUrl.trim()) {
      message.error('请先填写模型服务地址');
      return;
    }
    loading.value = true;
    try {
      const result = await api.fetchModelList({ base_url: baseUrl.trim(), api_key: apiKey || undefined });
      models.value = (result.models || []).map((id: string) => ({ id }));
      if (models.value.length === 0) {
        message.warning(result.detail || '该端点未返回任何模型');
      }
    } catch {
      message.error('获取模型列表失败');
    } finally {
      loading.value = false;
    }
  };

  const reset = () => {
    models.value = [];
  };

  return { models, loading, fetchModels, reset };
}
