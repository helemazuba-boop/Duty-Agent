/**
 * 对外链接的单一出处（「关于」页、隐私入口、商店列表共用）。
 *
 * 上架要求隐私政策有**无需登录、长期可访问的 HTTPS 地址**。当前指向仓库里的
 * PRIVACY.md（GitHub 会渲染成网页，无需登录）。
 *
 * ⚠️ 分支说明：PRIVACY.md 目前只存在于 `independent-agent` 分支（origin/main 上
 * 还没有）。合并进 main 之后请把下面的分支段改成 `main`——分支被删掉会让商店列表里
 * 的隐私政策链接 404，而商店审核与后续更新都会复检这个链接。
 */
const REPO = 'https://github.com/helemazuba-boop/Duty-Agent';
const DOCS_BRANCH = 'independent-agent';

/** 隐私政策（Partner Center「隐私政策 URL」字段填这一个）。 */
export const PRIVACY_URL = `${REPO}/blob/${DOCS_BRANCH}/PRIVACY.md`;

/** 使用说明 / 帮助。 */
export const SUPPORT_URL = `${REPO}#readme`;

/** 问题反馈入口（含 AI 输出不当的举报）。 */
export const FEEDBACK_URL = `${REPO}/issues`;

/** 私密反馈渠道：隐私问题与涉及真实学生数据的材料走邮件，不进公开 issue。 */
export const SUPPORT_EMAIL = 'helemazuba@gmail.com';
